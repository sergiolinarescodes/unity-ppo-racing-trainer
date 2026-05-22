using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.Track.Ribbon;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Track.Loop
{
    /// <summary>
    /// A validated, ordered closed-loop track ready to race. Built by
    /// <see cref="ClosedLoopService"/> from the chain output of
    /// <see cref="TrackChainExtractor"/> when its first and last anchors
    /// quantize to the same world position.
    ///
    /// Anchors are listed once per slot (the wrap-around closing anchor is
    /// not duplicated). Cumulative arc length is parallel to <see cref="Anchors"/>:
    /// element i is the distance along the centerline from <see cref="Anchors"/>[0]
    /// to <see cref="Anchors"/>[i]. <see cref="TotalLength"/> includes the closing
    /// wrap segment from <see cref="Anchors"/>[^1] back to <see cref="Anchors"/>[0].
    /// </summary>
    public readonly record struct ClosedLoop(
        int Id,
        IReadOnlyList<TrackChainAnchor> Anchors,
        IReadOnlyList<float> CumulativeArcLength,
        IReadOnlyList<float> Curvature,
        float TotalLength,
        int LapStartAnchorIndex,
        Vector3 LapStartPosition,
        Vector3 LapStartTangent,
        LoopSectorization Sectors = default)
    {
        /// <summary>
        /// Default backward offset (m) applied to the canonical spawn position so
        /// the car starts a hair behind the gate — guarantees the first sector
        /// crossing registers as the start line, not as nothing.
        /// </summary>
        public const float DefaultSpawnBackOffset = 0.05f;

        /// <summary>
        /// Single source of truth for "where the car spawns / where the start
        /// line is drawn". Every consumer (TrainingDirector, scenarios, editor
        /// thumbnails, trainer overlays) MUST go through this helper — never
        /// recompute their own start anchor or apply their own offsets.
        /// </summary>
        public Pose GetCanonicalStartPose(float backOffset = DefaultSpawnBackOffset)
        {
            Vector3 t = LapStartTangent.sqrMagnitude > 1e-6f
                ? LapStartTangent.normalized
                : Vector3.forward;
            Vector3 pos = LapStartPosition - backOffset * t;
            Vector3 flat = new(t.x, 0f, t.z);
            Quaternion rot = flat.sqrMagnitude > 1e-6f
                ? Quaternion.LookRotation(flat.normalized, Vector3.up)
                : Quaternion.identity;
            return new Pose(pos, rot);
        }

        /// <summary>Heading (radians, atan2(x, z)) matching <see cref="GetCanonicalStartPose"/>.</summary>
        public float GetCanonicalStartHeading()
            => Mathf.Atan2(LapStartTangent.x, LapStartTangent.z);

        /// <summary>
        /// Sample a pose at the given arc-length along the centerline (world space).
        /// The arc is wrapped into [0, TotalLength). Used by the spawn-grid math so
        /// back-row cars stagger along the actual race line — a Euclidean
        /// back-projection from the longest-straight midpoint can cross a curve and
        /// land on an adjacent parallel segment on dense procedural loops.
        /// </summary>
        public Pose SamplePoseAtArc(float arc)
        {
            int n = Anchors?.Count ?? 0;
            if (n == 0 || CumulativeArcLength == null || CumulativeArcLength.Count != n || TotalLength <= 0f)
                return new Pose(LapStartPosition, Quaternion.identity);

            float L = TotalLength;
            arc = ((arc % L) + L) % L;

            // Binary search for the largest i with CumulativeArcLength[i] <= arc.
            // The wrap segment from Anchors[n-1] back to Anchors[0] covers
            // [CumulativeArcLength[n-1], L); SegEnd for the last segment is L.
            int lo = 0, hi = n - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) >> 1;
                if (CumulativeArcLength[mid] <= arc) lo = mid;
                else hi = mid - 1;
            }
            int i = lo;
            int j = (i + 1) % n;
            float segStart = CumulativeArcLength[i];
            float segEnd = (j == 0) ? L : CumulativeArcLength[j];
            float segLen = Mathf.Max(1e-6f, segEnd - segStart);
            float u = Mathf.Clamp01((arc - segStart) / segLen);

            var a = Anchors[i];
            var b = Anchors[j];
            Vector3 worldPos = Vector3.Lerp(a.WorldPos, b.WorldPos, u);
            Vector3 t = Vector3.Lerp(a.Tangent, b.Tangent, u);
            Vector3 flat = new(t.x, 0f, t.z);
            Quaternion rot = flat.sqrMagnitude > 1e-6f
                ? Quaternion.LookRotation(flat.normalized, Vector3.up)
                : Quaternion.identity;
            return new Pose(worldPos, rot);
        }
    }

    /// <summary>
    /// K equally-spaced micro-sectors along the centerline, rooted at
    /// <see cref="ClosedLoop.LapStartAnchorIndex"/>. Used by the ML training
    /// loop to gate lap completion on physical traversal (must hit S0..S(K-1)
    /// in order before counting a lap), defeating projection-wrap shortcuts
    /// where nearby track segments rebind arc-length across geographic gaps.
    ///
    /// Macro sectors group micro-sectors evenly when MicroCount % MacroCount
    /// == 0 (default K=9, M=3 → 3 micros per macro).
    /// </summary>
    public readonly record struct LoopSectorization(
        int MicroCount,
        float LapStartArc,
        float TotalLength,
        IReadOnlyList<int> MicroBoundaryAnchor,
        int MacroCount = 3)
    {
        public float MicroLength => MicroCount > 0 ? TotalLength / MicroCount : 0f;

        /// <summary>Sector index (0..MicroCount-1) for an arc-length along the loop.</summary>
        public int MicroSectorOf(float arc)
        {
            if (MicroCount <= 0 || TotalLength <= 0f) return 0;
            float delta = arc - LapStartArc;
            delta = ((delta % TotalLength) + TotalLength) % TotalLength;
            int s = (int)Mathf.Floor(delta / MicroLength);
            if (s < 0) s = 0;
            if (s >= MicroCount) s = MicroCount - 1;
            return s;
        }

        public int MacroSectorOf(int micro)
        {
            if (MacroCount <= 0 || MicroCount <= 0) return 0;
            int m = micro * MacroCount / MicroCount;
            return Mathf.Clamp(m, 0, MacroCount - 1);
        }
    }
}
