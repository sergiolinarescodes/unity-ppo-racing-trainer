using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.Terrain;
using UnityPpoRacingTrainer.Core.Track;
using UnityPpoRacingTrainer.Core.Track.Loop;
using UnityPpoRacingTrainer.Core.Track.Ribbon;
using Unidad.Core.EventBus;
using Unidad.Core.Systems;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.AiDriver.Loop
{
    internal sealed class TrackQueryService : SystemServiceBase, ITrackQueryService
    {
        // Search window around the hint anchor — covers up to ~20 anchors of drift
        // per frame at typical car speeds (15 m/s × 0.02s = 0.3m, < one anchor).
        private const int HintWindowRadius = 20;
        // Roughly one car-width buffer past the curb. The IsOffTrack flag is
        // used by reward + episode-timer logic; cars within this buffer are
        // still considered "on-track" for those purposes (so small corner
        // cuts are not penalised by the off-track logic). The physics surface
        // model in CarSimulationService ramps drag and the speed cap
        // continuously from the curb itself, regardless of this flag — so
        // there is still a gradual slowdown inside the buffer.
        private const float OffTrackTolerance = 1.0f;

        private readonly IClosedLoopService _loopService;
        private readonly ITrackCollisionService _collision;
        private readonly ITrackPlacementService _placement;
        private readonly ITerrainService _terrain;
        private readonly TrackChainExtractor _extractor;
        private ClosedLoop _cachedLoop;
        private bool _hasLoop;

        // Open-ribbon fallback: the longest extracted chain across all placed
        // pieces. Lets Project + SampleLookaheadAt feed real centerline samples
        // to the policy before the player has closed the loop — otherwise the
        // observation block goes all-zero and the model (trained exclusively on
        // closed loops) collapses to a hard brake on out-of-distribution input.
        private TrackChainAnchor[] _openAnchors;
        private float[] _openArcLen;
        private float[] _openCurvature;
        private float _openTotalLen;
        private bool _hasOpenChain;

        public TrackQueryService(IEventBus eventBus, IClosedLoopService loopService,
            ITrackCollisionService collision = null,
            ITrackPlacementService placement = null,
            ITrackPieceCatalog catalog = null,
            ITerrainService terrain = null) : base(eventBus)
        {
            _loopService = loopService;
            _collision = collision;
            _placement = placement;
            _terrain = terrain;
            if (catalog != null) _extractor = new TrackChainExtractor(catalog);
            Subscribe<LoopClosedEvent>(_ => RefreshCache());
            Subscribe<LoopOpenedEvent>(_ => RefreshCache());
            if (_placement != null && _extractor != null)
            {
                Subscribe<TrackPiecePlacedEvent>(_ => RefreshCache());
                Subscribe<TrackPieceRemovedEvent>(_ => RefreshCache());
            }
            RefreshCache();
        }

        public bool HasLoop => _hasLoop;
        public bool HasPath => _hasLoop || _hasOpenChain;
        public float TotalPathLength => _hasLoop ? _cachedLoop.TotalLength : _openTotalLen;

        public TrackProjection Project(Vector3 worldPos, int hintAnchorIndex = -1)
        {
            if (_hasLoop) return ProjectOnLoop(worldPos, hintAnchorIndex);
            if (_hasOpenChain) return ProjectOnOpenChain(worldPos, hintAnchorIndex);
            return default;
        }

        private TrackProjection ProjectOnLoop(Vector3 worldPos, int hintAnchorIndex)
        {
            var anchors = _cachedLoop.Anchors;
            int n = anchors.Count;
            if (n < 2) return default;

            int bestIdx = FindNearestAnchor(worldPos, anchors, n, hintAnchorIndex);

            int next = (bestIdx + 1) % n;
            int prev = (bestIdx - 1 + n) % n;

            var fwd = ProjectOntoSegment(worldPos, anchors[bestIdx], anchors[next]);
            var bwd = ProjectOntoSegment(worldPos, anchors[prev], anchors[bestIdx]);

            int segIdx;
            float segT;
            Vector3 projPoint;
            Vector3 tangent;
            float halfWidth;

            if (fwd.distSq <= bwd.distSq)
            {
                segIdx = bestIdx;
                segT = fwd.t;
                projPoint = fwd.projPoint;
                tangent = fwd.tangent;
                halfWidth = fwd.halfWidth;
            }
            else
            {
                segIdx = prev;
                segT = bwd.t;
                projPoint = bwd.projPoint;
                tangent = bwd.tangent;
                halfWidth = bwd.halfWidth;
            }

            float segLen = SegmentLengthXZ(anchors[segIdx], anchors[(segIdx + 1) % n]);
            float arcAlong = _cachedLoop.CumulativeArcLength[segIdx] + segT * segLen;

            // Signed lateral: + = left of tangent, - = right (XZ-only, ignore Y).
            Vector2 t2 = new(tangent.x, tangent.z);
            Vector2 right = new(t2.y, -t2.x);
            Vector2 delta = new(worldPos.x - projPoint.x, worldPos.z - projPoint.z);
            float lateral = Vector2.Dot(delta, right);

            // Surface kind comes from the collision service (kerbs are placed as
            // QuadKerbZone instances during track piece spawn). When the service
            // is absent (early tests, debug scenarios), default to asphalt.
            var surface = _collision != null
                ? _collision.SampleSurface(worldPos)
                : SurfaceKind.Asphalt;

            return new TrackProjection(
                NearestAnchorIndex: bestIdx,
                ArcLengthAlong: arcAlong,
                ProjectedPoint: projPoint,
                Tangent: tangent,
                HalfWidth: halfWidth,
                SignedLateralOffset: lateral,
                ElevationAtPoint: projPoint.y,
                IsOffTrack: Mathf.Abs(lateral) > halfWidth + OffTrackTolerance,
                Surface: surface);
        }

        private TrackProjection ProjectOnOpenChain(Vector3 worldPos, int hintAnchorIndex)
        {
            var anchors = _openAnchors;
            int n = anchors.Length;
            if (n < 2) return default;

            int bestIdx = FindNearestAnchorOpen(worldPos, anchors, n, hintAnchorIndex);

            // Prefer the forward segment when at the head, backward segment at
            // the tail; otherwise pick whichever segment gives the smaller
            // perpendicular distance. Clamped — never wraps.
            (Vector3 projPoint, Vector3 tangent, float halfWidth, float t, float distSq) fwd =
                bestIdx + 1 < n
                    ? ProjectOntoSegment(worldPos, anchors[bestIdx], anchors[bestIdx + 1])
                    : (anchors[bestIdx].WorldPos, anchors[bestIdx].Tangent, anchors[bestIdx].HalfWidth, 1f, float.MaxValue);
            (Vector3 projPoint, Vector3 tangent, float halfWidth, float t, float distSq) bwd =
                bestIdx > 0
                    ? ProjectOntoSegment(worldPos, anchors[bestIdx - 1], anchors[bestIdx])
                    : (anchors[bestIdx].WorldPos, anchors[bestIdx].Tangent, anchors[bestIdx].HalfWidth, 0f, float.MaxValue);

            int segIdx;
            float segT;
            Vector3 projPoint, tangent;
            float halfWidth;

            if (fwd.distSq <= bwd.distSq)
            {
                segIdx = bestIdx;
                segT = fwd.t;
                projPoint = fwd.projPoint;
                tangent = fwd.tangent;
                halfWidth = fwd.halfWidth;
            }
            else
            {
                segIdx = Mathf.Max(0, bestIdx - 1);
                segT = bwd.t;
                projPoint = bwd.projPoint;
                tangent = bwd.tangent;
                halfWidth = bwd.halfWidth;
            }

            float segLen = segIdx + 1 < n ? SegmentLengthXZ(anchors[segIdx], anchors[segIdx + 1]) : 0f;
            float arcAlong = _openArcLen[segIdx] + segT * segLen;

            Vector2 t2 = new(tangent.x, tangent.z);
            Vector2 right = new(t2.y, -t2.x);
            Vector2 delta = new(worldPos.x - projPoint.x, worldPos.z - projPoint.z);
            float lateral = Vector2.Dot(delta, right);

            var surface = _collision != null
                ? _collision.SampleSurface(worldPos)
                : SurfaceKind.Asphalt;

            return new TrackProjection(
                NearestAnchorIndex: bestIdx,
                ArcLengthAlong: arcAlong,
                ProjectedPoint: projPoint,
                Tangent: tangent,
                HalfWidth: halfWidth,
                SignedLateralOffset: lateral,
                ElevationAtPoint: projPoint.y,
                IsOffTrack: Mathf.Abs(lateral) > halfWidth + OffTrackTolerance,
                Surface: surface);
        }

        public void SampleLookahead(int startAnchorIndex, float distanceMeters, int sampleCount, Span<CenterlineSample> output)
        {
            if (!_hasLoop || sampleCount <= 0 || output.Length < sampleCount) return;

            var anchors = _cachedLoop.Anchors;
            var arcLen = _cachedLoop.CumulativeArcLength;
            var curvature = _cachedLoop.Curvature;
            int n = anchors.Count;
            float total = _cachedLoop.TotalLength;
            if (n < 2 || total < 1e-3f) return;

            int normalizedStart = ((startAnchorIndex % n) + n) % n;
            float startArc = arcLen[normalizedStart];
            float deltaS = distanceMeters / sampleCount;

            for (int i = 0; i < sampleCount; i++)
            {
                float targetArc = (startArc + (i + 1) * deltaS) % total;
                output[i] = SampleAtArc(targetArc, anchors, arcLen, curvature, n, total);
            }
        }

        public void SampleLookaheadAt(int startAnchorIndex, ReadOnlySpan<float> arcOffsetsMeters, Span<CenterlineSample> output)
        {
            if (output.Length < arcOffsetsMeters.Length) return;

            if (_hasLoop)
            {
                var anchors = _cachedLoop.Anchors;
                var arcLen = _cachedLoop.CumulativeArcLength;
                var curvature = _cachedLoop.Curvature;
                int n = anchors.Count;
                float total = _cachedLoop.TotalLength;
                if (n < 2 || total < 1e-3f) return;

                int normalizedStart = ((startAnchorIndex % n) + n) % n;
                float startArc = arcLen[normalizedStart];

                for (int i = 0; i < arcOffsetsMeters.Length; i++)
                {
                    float raw = startArc + arcOffsetsMeters[i];
                    float targetArc = ((raw % total) + total) % total;
                    output[i] = SampleAtArc(targetArc, anchors, arcLen, curvature, n, total);
                }
                return;
            }

            if (_hasOpenChain)
            {
                int n = _openAnchors.Length;
                float total = _openTotalLen;
                if (n < 2 || total < 1e-3f) return;

                int normalizedStart = Mathf.Clamp(startAnchorIndex, 0, n - 1);
                float startArc = _openArcLen[normalizedStart];

                for (int i = 0; i < arcOffsetsMeters.Length; i++)
                {
                    // No arc clamp — SampleAtArcOpen extrapolates straight along
                    // the tail/head tangents when targetArc exits the chain
                    // range, producing in-distribution lookahead samples (the
                    // training distribution is closed loops longer than the
                    // 1.5s × ref-speed ≈ 47m horizon, so far-samples were
                    // always "real road" — a flat-plateau or zero-fill is
                    // out-of-distribution and shifts policy timing).
                    float targetArc = startArc + arcOffsetsMeters[i];
                    output[i] = SampleAtArcOpen(targetArc, _openAnchors, _openArcLen, _openCurvature, n, total);
                }
            }
        }

        public float GetElevationAt(Vector3 worldPos)
        {
            if (!HasPath) return 0f;
            return Project(worldPos).ElevationAtPoint;
        }

        // -------------------- internals --------------------

        private static int FindNearestAnchor(Vector3 worldPos, IReadOnlyList<TrackChainAnchor> anchors, int n, int hint)
        {
            int searchStart;
            int searchCount;
            if (hint >= 0)
            {
                searchStart = ((hint - HintWindowRadius) % n + n) % n;
                searchCount = Mathf.Min(2 * HintWindowRadius, n);
            }
            else
            {
                searchStart = 0;
                searchCount = n;
            }

            int bestIdx = -1;
            float bestDistSq = float.MaxValue;
            for (int s = 0; s < searchCount; s++)
            {
                int i = (searchStart + s) % n;
                float dx = anchors[i].WorldPos.x - worldPos.x;
                float dz = anchors[i].WorldPos.z - worldPos.z;
                float d = dx * dx + dz * dz;
                if (d < bestDistSq)
                {
                    bestDistSq = d;
                    bestIdx = i;
                }
            }
            return bestIdx >= 0 ? bestIdx : 0;
        }

        private static (Vector3 projPoint, Vector3 tangent, float halfWidth, float t, float distSq) ProjectOntoSegment(
            Vector3 p, TrackChainAnchor a, TrackChainAnchor b)
        {
            float abx = b.WorldPos.x - a.WorldPos.x;
            float abz = b.WorldPos.z - a.WorldPos.z;
            float len2 = abx * abx + abz * abz;
            if (len2 < 1e-6f)
            {
                float dxa = p.x - a.WorldPos.x;
                float dza = p.z - a.WorldPos.z;
                return (a.WorldPos, a.Tangent, a.HalfWidth, 0f, dxa * dxa + dza * dza);
            }

            float apx = p.x - a.WorldPos.x;
            float apz = p.z - a.WorldPos.z;
            float t = Mathf.Clamp01((apx * abx + apz * abz) / len2);

            Vector3 ab = b.WorldPos - a.WorldPos;
            Vector3 proj = a.WorldPos + ab * t;

            Vector3 tangent = new Vector3(abx, ab.y, abz).normalized;
            float halfWidth = Mathf.Lerp(a.HalfWidth, b.HalfWidth, t);

            float dprojx = proj.x - p.x;
            float dprojz = proj.z - p.z;
            return (proj, tangent, halfWidth, t, dprojx * dprojx + dprojz * dprojz);
        }

        private static float SegmentLengthXZ(TrackChainAnchor a, TrackChainAnchor b)
        {
            float dx = b.WorldPos.x - a.WorldPos.x;
            float dz = b.WorldPos.z - a.WorldPos.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private static CenterlineSample SampleAtArc(
            float targetArc,
            IReadOnlyList<TrackChainAnchor> anchors,
            IReadOnlyList<float> arcLen,
            IReadOnlyList<float> curvature,
            int n,
            float total)
        {
            int seg = 0;
            for (int j = 0; j < n; j++)
            {
                float lo = arcLen[j];
                float hi = (j + 1 < n) ? arcLen[j + 1] : total;
                if (targetArc >= lo && targetArc < hi)
                {
                    seg = j;
                    break;
                }
            }

            int next = (seg + 1) % n;
            float segLo = arcLen[seg];
            float segHi = (next == 0) ? total : arcLen[next];
            float segLen = segHi - segLo;
            float t = segLen > 1e-6f ? (targetArc - segLo) / segLen : 0f;

            Vector3 pos = Vector3.Lerp(anchors[seg].WorldPos, anchors[next].WorldPos, t);
            Vector3 tan = Vector3.Slerp(anchors[seg].Tangent, anchors[next].Tangent, t);
            float halfW = Mathf.Lerp(anchors[seg].HalfWidth, anchors[next].HalfWidth, t);
            float curv = Mathf.Lerp(curvature[seg], curvature[next], t);

            return new CenterlineSample(pos, tan, halfW, curv, pos.y);
        }

        private static int FindNearestAnchorOpen(Vector3 worldPos, TrackChainAnchor[] anchors, int n, int hint)
        {
            int searchStart, searchEnd;
            if (hint >= 0)
            {
                searchStart = Mathf.Max(0, hint - HintWindowRadius);
                searchEnd = Mathf.Min(n - 1, hint + HintWindowRadius);
            }
            else
            {
                searchStart = 0;
                searchEnd = n - 1;
            }

            int bestIdx = -1;
            float bestDistSq = float.MaxValue;
            for (int i = searchStart; i <= searchEnd; i++)
            {
                float dx = anchors[i].WorldPos.x - worldPos.x;
                float dz = anchors[i].WorldPos.z - worldPos.z;
                float d = dx * dx + dz * dz;
                if (d < bestDistSq)
                {
                    bestDistSq = d;
                    bestIdx = i;
                }
            }
            return bestIdx >= 0 ? bestIdx : 0;
        }

        private static CenterlineSample SampleAtArcOpen(
            float targetArc,
            TrackChainAnchor[] anchors,
            float[] arcLen,
            float[] curvature,
            int n,
            float total)
        {
            if (n < 2)
            {
                var only = anchors[Mathf.Max(0, n - 1)];
                return new CenterlineSample(only.WorldPos, only.Tangent, only.HalfWidth, 0f, only.WorldPos.y);
            }
            // Past the tail: extrapolate linearly along the last anchor's
            // tangent. The trained policy reads all 5 lookahead samples as
            // live road; a flat-plateau "frozen at tail" reading is an
            // out-of-distribution "road ends" signal it never saw during
            // training (closed loops are always longer than the 1.5s × ref
            // speed ≈ 47m horizon). Curvature 0 keeps the far-sample reading
            // "straight ahead, no turn coming" — a clean in-distribution
            // observation. The chain itself does NOT grow; this only affects
            // the observation sample.
            if (targetArc >= total - 1e-6f)
            {
                var last = anchors[n - 1];
                float overshoot = targetArc - total;
                Vector3 extPos = last.WorldPos + last.Tangent * overshoot;
                return new CenterlineSample(extPos, last.Tangent, last.HalfWidth, 0f, extPos.y);
            }
            // Before the head: extrapolate backwards along the first anchor's
            // tangent. Symmetric reasoning — the policy's "0s sample" can
            // briefly land behind the head right after spawn while projection
            // settles. Curvature 0 matches the open-road-behind expectation.
            if (targetArc <= 0f)
            {
                var first = anchors[0];
                Vector3 extPos = first.WorldPos + first.Tangent * targetArc;
                return new CenterlineSample(extPos, first.Tangent, first.HalfWidth, 0f, extPos.y);
            }

            int seg = 0;
            for (int j = 0; j < n - 1; j++)
            {
                float lo = arcLen[j];
                float hi = arcLen[j + 1];
                if (targetArc >= lo && targetArc < hi)
                {
                    seg = j;
                    break;
                }
            }
            int next = seg + 1;
            float segLo = arcLen[seg];
            float segHi = arcLen[next];
            float segLen = segHi - segLo;
            float t = segLen > 1e-6f ? (targetArc - segLo) / segLen : 0f;

            Vector3 pos = Vector3.Lerp(anchors[seg].WorldPos, anchors[next].WorldPos, t);
            Vector3 tan = Vector3.Slerp(anchors[seg].Tangent, anchors[next].Tangent, t);
            float halfW = Mathf.Lerp(anchors[seg].HalfWidth, anchors[next].HalfWidth, t);
            float curv = Mathf.Lerp(curvature[seg], curvature[next], t);
            return new CenterlineSample(pos, tan, halfW, curv, pos.y);
        }

        private void RefreshCache()
        {
            _hasLoop = _loopService.TryGetCurrentLoop(out _cachedLoop);
            if (_hasLoop)
            {
                _hasOpenChain = false;
                _openAnchors = null;
                _openArcLen = null;
                _openCurvature = null;
                _openTotalLen = 0f;
                return;
            }
            RebuildOpenChain();
        }

        private void RebuildOpenChain()
        {
            _hasOpenChain = false;
            _openAnchors = null;
            _openArcLen = null;
            _openCurvature = null;
            _openTotalLen = 0f;
            if (_extractor == null || _placement == null) return;

            float cellSize = _terrain != null && _terrain.IsInitialized ? _terrain.CellSize : 1f;
            var chains = _extractor.Extract(_placement.Placed, cellSize);
            if (chains.Count == 0) return;

            int longestIdx = 0;
            for (int i = 1; i < chains.Count; i++)
                if (chains[i].Length > chains[longestIdx].Length) longestIdx = i;
            var anchors = chains[longestIdx];
            int n = anchors.Length;
            if (n < 2) return;

            var arcLen = new float[n];
            arcLen[0] = 0f;
            for (int i = 1; i < n; i++)
                arcLen[i] = arcLen[i - 1] + SegmentLengthXZ(anchors[i - 1], anchors[i]);
            float total = arcLen[n - 1];
            if (total < 1e-3f) return;

            // Finite-difference curvature: dHeading / dArc using neighbour
            // tangents. Must match ClosedLoopService's sign convention (uses
            // Vector3.SignedAngle with Vector3.up) — the policy was trained on
            // closed-loop curvature, so a sign flip here makes every curve read
            // mirrored and the model "doesn't see" the turn. Endpoints inherit
            // their neighbour's value so the plateau-clamp doesn't introduce a
            // phantom curvature spike.
            var curv = new float[n];
            for (int i = 1; i < n - 1; i++)
            {
                Vector3 t1 = new(anchors[i - 1].Tangent.x, 0f, anchors[i - 1].Tangent.z);
                Vector3 t2 = new(anchors[i + 1].Tangent.x, 0f, anchors[i + 1].Tangent.z);
                float dHead = Vector3.SignedAngle(t1, t2, Vector3.up) * Mathf.Deg2Rad;
                float ds = arcLen[i + 1] - arcLen[i - 1];
                curv[i] = ds > 1e-6f ? dHead / ds : 0f;
            }
            curv[0] = n >= 3 ? curv[1] : 0f;
            curv[n - 1] = n >= 3 ? curv[n - 2] : 0f;

            _openAnchors = anchors;
            _openArcLen = arcLen;
            _openCurvature = curv;
            _openTotalLen = total;
            _hasOpenChain = true;
        }
    }
}
