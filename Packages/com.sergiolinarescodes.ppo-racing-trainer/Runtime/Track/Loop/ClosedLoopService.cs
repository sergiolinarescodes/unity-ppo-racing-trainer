using UnityPpoRacingTrainer.Core.Terrain;
using UnityPpoRacingTrainer.Core.Track.Ribbon;
using Unidad.Core.EventBus;
using Unidad.Core.Systems;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Track.Loop
{
    internal sealed class ClosedLoopService : SystemServiceBase, IClosedLoopService
    {
        private const int MinClosedAnchorCount = 4;

        private readonly ITrackPlacementService _placement;
        private readonly ITerrainService _terrain;
        private readonly TrackChainExtractor _extractor;

        private ClosedLoop _currentLoop;
        private bool _hasLoop;
        private int _nextLoopId = 1;

        public ClosedLoopService(
            IEventBus eventBus,
            ITrackPlacementService placement,
            ITrackPieceCatalog catalog,
            ITerrainService terrain = null) : base(eventBus)
        {
            _placement = placement;
            _terrain = terrain;
            _extractor = new TrackChainExtractor(catalog);

            Subscribe<TrackPiecePlacedEvent>(_ => Rebuild());
            Subscribe<TrackPieceRemovedEvent>(_ => Rebuild());
        }

        public bool IsLoopClosed => _hasLoop;

        public bool TryGetCurrentLoop(out ClosedLoop loop)
        {
            loop = _currentLoop;
            return _hasLoop;
        }

        private void Rebuild()
        {
            float cellSize = _terrain != null && _terrain.IsInitialized
                ? _terrain.CellSize
                : 1f;
            // Closure tolerance scales with cell size so a long stadium loop at cellSize=2
            // doesn't misread accumulated drift as still-open.
            float closureThreshold = TrackPieceConstants.PortQuantizeGridSize * 2f * cellSize;
            var chains = _extractor.Extract(_placement.Placed, cellSize);

            ClosedLoop best = default;
            bool found = false;
            float bestLength = 0f;

            for (int i = 0; i < chains.Count; i++)
            {
                var chain = chains[i];
                if (chain.Length < MinClosedAnchorCount) continue;
                if (Vector3.Distance(chain[0].WorldPos, chain[^1].WorldPos) > closureThreshold) continue;

                float length = SegmentLength(chain) + Vector3.Distance(chain[^1].WorldPos, chain[0].WorldPos);
                if (!found || length > bestLength)
                {
                    best = BuildClosedLoop(chain, length);
                    bestLength = length;
                    found = true;
                }
            }

            if (found)
            {
                bool wasClosed = _hasLoop;
                int id = wasClosed ? _currentLoop.Id : _nextLoopId++;
                _currentLoop = best with { Id = id };
                _hasLoop = true;
                if (!wasClosed)
                {
                    Publish(new LoopClosedEvent(id, _currentLoop.TotalLength, _currentLoop.Anchors.Count));
                }
            }
            else if (_hasLoop)
            {
                int previous = _currentLoop.Id;
                _currentLoop = default;
                _hasLoop = false;
                Publish(new LoopOpenedEvent(previous));
            }
        }

        private static ClosedLoop BuildClosedLoop(TrackChainAnchor[] anchors, float totalLength)
        {
            // Canonicalize traversal so chain extractor's arbitrary CW/CCW pick
            // doesn't randomize LapStartTangent. Without this, generator-driven
            // placement insertion order silently flipped spawn heading 180° per
            // seed — trainer drove backward ~50% of episodes, crashes mirrored
            // across the circuit in the Python analysis vs the trainer-test view.
            EnsureCcwInXZ(anchors);

            int n = anchors.Length;

            var arcLength = new float[n];
            arcLength[0] = 0f;
            for (int i = 1; i < n; i++)
            {
                arcLength[i] = arcLength[i - 1] +
                               Vector3.Distance(anchors[i].WorldPos, anchors[i - 1].WorldPos);
            }

            var curvature = new float[n];
            for (int i = 0; i < n; i++)
            {
                int prev = (i - 1 + n) % n;
                int next = (i + 1) % n;

                Vector3 t1 = Flatten(anchors[prev].Tangent);
                Vector3 t2 = Flatten(anchors[next].Tangent);

                float angle = Vector3.SignedAngle(t1, t2, Vector3.up) * Mathf.Deg2Rad;
                float ds = SpanArcLength(arcLength, totalLength, prev, next);
                curvature[i] = ds > 1e-4f ? angle / ds : 0f;
            }

            // Longest contiguous low-curvature run = "straight". Anchor the lap
            // start at its midpoint so the agent always spawns on the cleanest
            // forward stretch with the same start/finish line every episode.
            int lapStartIdx = FindLongestStraightMidpoint(arcLength, curvature, totalLength, out bool straightFound);
            if (!straightFound)
            {
                Debug.LogWarning($"[ClosedLoopService] No straight ≥ threshold found ({n} anchors, {totalLength:F1}m). Falling back to longest single segment.");
            }

            // K micro-sectors evenly spaced by arc length, rooted at lapStartArc.
            const int K = 9;
            const int MacroK = 3;
            float lapStartArc = arcLength[lapStartIdx];
            var microAnchor = new int[K];
            for (int s = 0; s < K; s++)
            {
                float a = WrapArc(lapStartArc + s * totalLength / K, totalLength);
                microAnchor[s] = FindAnchorAtOrBefore(arcLength, totalLength, a);
            }
            var sectors = new LoopSectorization(K, lapStartArc, totalLength, microAnchor, MacroK);

            return new ClosedLoop(
                Id: 0,
                Anchors: anchors,
                CumulativeArcLength: arcLength,
                Curvature: curvature,
                TotalLength: totalLength,
                LapStartAnchorIndex: lapStartIdx,
                LapStartPosition: anchors[lapStartIdx].WorldPos,
                LapStartTangent: anchors[lapStartIdx].Tangent,
                Sectors: sectors);
        }

        // Convention: CCW in XZ (shoelace twiceArea >= 0). If the extractor
        // walked the loop backward, reverse anchors and negate every tangent
        // so the LapStartTangent — and therefore the canonical spawn heading —
        // is fully determined by loop geometry, not by placement insertion order.
        private static void EnsureCcwInXZ(TrackChainAnchor[] anchors)
        {
            int n = anchors.Length;
            if (n < 3) return;
            float twiceArea = 0f;
            for (int i = 0; i < n; i++)
            {
                var a = anchors[i].WorldPos;
                var b = anchors[(i + 1) % n].WorldPos;
                twiceArea += a.x * b.z - b.x * a.z;
            }
            if (twiceArea >= 0f) return;
            System.Array.Reverse(anchors);
            for (int i = 0; i < n; i++)
            {
                var a = anchors[i];
                anchors[i] = new TrackChainAnchor(a.WorldPos, -a.Tangent, a.HalfWidth);
            }
        }

        // 1/m. Recipe curves register κ ~0.3-0.5; straights ~0. Anchors with
        // |κ| below this count as straight runs.
        private const float StraightCurvatureThreshold = 0.05f;

        private static int FindLongestStraightMidpoint(
            float[] arcLength, float[] curvature, float totalLength, out bool straightFound)
        {
            int n = arcLength.Length;
            int bestStart = -1, bestEnd = -1;
            float bestLen = 0f;

            // Scan with wrap: track current run length, start index. To handle
            // a straight crossing index 0, we run 2n iterations and break only
            // when we re-enter a run we've already evaluated.
            int runStart = -1;
            float runLen = 0f;
            for (int k = 0; k < 2 * n; k++)
            {
                int i = k % n;
                if (Mathf.Abs(curvature[i]) < StraightCurvatureThreshold)
                {
                    if (runStart < 0) { runStart = i; runLen = 0f; }
                    int next = (i + 1) % n;
                    runLen += SpanArcLength(arcLength, totalLength, i, next);
                    if (runLen > bestLen)
                    {
                        bestLen = runLen;
                        bestStart = runStart;
                        bestEnd = i;
                    }
                    if (k >= n && i == bestStart) break;
                }
                else
                {
                    runStart = -1;
                    runLen = 0f;
                }
            }

            straightFound = bestLen > 0f && bestStart >= 0;
            if (!straightFound)
            {
                // Fallback: longest single anchor-to-anchor segment.
                float maxSeg = -1f;
                int maxIdx = 0;
                for (int i = 0; i < n; i++)
                {
                    int next = (i + 1) % n;
                    float seg = SpanArcLength(arcLength, totalLength, i, next);
                    if (seg > maxSeg) { maxSeg = seg; maxIdx = i; }
                }
                bestStart = maxIdx;
                bestEnd = (maxIdx + 1) % n;
                bestLen = maxSeg;
            }

            // Midpoint arc along the longest run.
            float startArc = arcLength[bestStart];
            float endArc = arcLength[bestEnd];
            float midArc = WrapArc(startArc + bestLen * 0.5f, totalLength);
            // bestEnd guards: if startArc < endArc no wrap; otherwise the run crosses index 0.
            return FindAnchorAtOrBefore(arcLength, totalLength, midArc);
        }

        private static int FindAnchorAtOrBefore(float[] arcLength, float totalLength, float targetArc)
        {
            // arcLength is monotonic ascending in [0, totalLength). targetArc in [0, totalLength).
            int n = arcLength.Length;
            int lo = 0, hi = n - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) >> 1;
                if (arcLength[mid] <= targetArc) lo = mid;
                else hi = mid - 1;
            }
            return lo;
        }

        private static float WrapArc(float arc, float totalLength)
        {
            if (totalLength <= 0f) return 0f;
            float a = arc % totalLength;
            if (a < 0f) a += totalLength;
            return a;
        }

        private static float SegmentLength(TrackChainAnchor[] anchors)
        {
            float l = 0f;
            for (int i = 1; i < anchors.Length; i++)
            {
                l += Vector3.Distance(anchors[i].WorldPos, anchors[i - 1].WorldPos);
            }
            return l;
        }

        // Distance along the loop from index `prev` to index `next`, accounting
        // for the wrap-around closing segment when next == 0.
        private static float SpanArcLength(float[] arcLength, float totalLength, int prev, int next)
        {
            if (next == 0)
            {
                return totalLength - arcLength[prev];
            }
            return arcLength[next] - arcLength[prev];
        }

        private static Vector3 Flatten(Vector3 v) => new(v.x, 0f, v.z);
    }

    /// <summary>
    /// Renders K=9 vertical posts at the loop's micro-sector boundaries plus a
    /// thicker red gate at the start/finish line (sector 0). Color encodes the
    /// macro sector (0=red, 1=green, 2=cyan) so sector-checkpoint lap counting
    /// is auditable visually. SINGLE source for start-line + sector visuals —
    /// every closed-loop scene/scenario should mount this so trainer / editor /
    /// inference all show the exact same gate the simulator uses.
    /// </summary>
    internal sealed class SectorBoundaryDebugRenderer : MonoBehaviour
    {
        private const float PostHeight = 4f;
        private const float StartGateHeight = 6f;
        private const float StartGateWidth = 0.18f;
        private const float NormalWidth = 0.08f;

        private IClosedLoopService _loop;
        private LineRenderer[] _lines;
        private int _builtForLoopId;

        public void Bind(IClosedLoopService loop)
        {
            _loop = loop;
            _builtForLoopId = -1;
        }

        /// <summary>
        /// Mount + bind in one call. Call this from any scenario/scene that
        /// wants the canonical start-line + sector visualization. The component
        /// idles cheaply when no loop is closed, so it's safe to mount eagerly.
        /// </summary>
        public static SectorBoundaryDebugRenderer MountOn(GameObject parent, IClosedLoopService loop)
        {
            if (parent == null) return null;
            var dbg = parent.AddComponent<SectorBoundaryDebugRenderer>();
            dbg.Bind(loop);
            return dbg;
        }

        private void EnsureBuilt(in ClosedLoop closed)
        {
            if (_lines != null && _builtForLoopId == closed.Id) return;

            // Tear down previous (loop regenerated → sector count may differ).
            if (_lines != null)
            {
                for (int i = 0; i < _lines.Length; i++)
                    if (_lines[i] != null) Destroy(_lines[i].gameObject);
            }

            int k = Mathf.Max(closed.Sectors.MicroCount, 0);
            _lines = new LineRenderer[k];
            var shader = Shader.Find("Sprites/Default");
            for (int i = 0; i < k; i++)
            {
                var go = new GameObject($"SectorBoundary{i}");
                go.transform.SetParent(transform, false);
                var lr = go.AddComponent<LineRenderer>();
                bool isStart = i == 0;
                lr.startWidth = isStart ? StartGateWidth : NormalWidth;
                lr.endWidth = isStart ? StartGateWidth : NormalWidth;
                lr.material = new Material(shader);
                lr.positionCount = 2;
                lr.useWorldSpace = true;
                _lines[i] = lr;
            }
            _builtForLoopId = closed.Id;
        }

        private void Update()
        {
            if (_loop == null || !_loop.TryGetCurrentLoop(out var closed)
                || closed.Sectors.MicroCount <= 0
                || closed.Anchors == null || closed.Anchors.Count == 0)
            {
                if (_lines != null)
                    for (int i = 0; i < _lines.Length; i++)
                        if (_lines[i] != null) _lines[i].enabled = false;
                return;
            }

            EnsureBuilt(closed);

            int k = closed.Sectors.MicroCount;
            for (int i = 0; i < k; i++)
            {
                int aIdx = Mathf.Clamp(closed.Sectors.MicroBoundaryAnchor[i], 0, closed.Anchors.Count - 1);
                Vector3 basePos = closed.Anchors[aIdx].WorldPos;
                bool isStart = i == 0;
                float h = isStart ? StartGateHeight : PostHeight;
                Vector3 top = basePos + Vector3.up * h;

                int macro = closed.Sectors.MacroSectorOf(i);
                Color c = macro == 0 ? Color.red : macro == 1 ? Color.green : Color.cyan;
                if (isStart) c = Color.red;

                _lines[i].enabled = true;
                _lines[i].SetPosition(0, basePos);
                _lines[i].SetPosition(1, top);
                _lines[i].startColor = c;
                _lines[i].endColor = c;
            }
        }
    }
}
