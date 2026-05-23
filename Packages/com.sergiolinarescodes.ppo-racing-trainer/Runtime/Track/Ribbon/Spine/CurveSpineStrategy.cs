using System.Collections.Generic;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Track.Ribbon.Spine
{
    /// <summary>
    /// Quarter-arc curves (1×1) and long curves (1×2 = straight + arc). Mirrored variants
    /// (LeftCurve_1x1) reuse this strategy and get flipped via <c>def.MirrorX</c> in the
    /// chain extractor's world transform — same way the mesh strategy reuses CurveMeshStrategy.
    /// </summary>
    internal sealed class CurveSpineStrategy : ITrackSpineSampler
    {
        public TrackPieceFamily Family => TrackPieceFamily.Curve;

        public IReadOnlyList<SpineSample> Sample(TrackPieceDefinition def)
        {
            // Wide arcs (W=2) are filtered by the extractor; per-piece slabs render them.
            if (def.Dimensions.Width != 1) return null;

            int L = def.Dimensions.Length;
            float hw = TrackPieceConstants.LaneHalfWidth;
            int segments = TrackPieceConstants.DefaultArcSegments;
            var list = new List<SpineSample>();

            if (L == 1)
            {
                // Canonical NE quarter-arc. Center (1,0) is the SE corner of the tile —
                // arc enters south face, exits east face, R = 0.5.
                AppendArc(list, new Vector2(1f, 0f), 0.5f, Mathf.PI, Mathf.PI * 0.5f, segments, hw);
                return list;
            }
            if (L == 2)
            {
                // Long curve: straight lead-in (tile 0) + quarter-arc (tile 1).
                list.Add(new SpineSample(new Vector3(0.5f, 0f, 0f), new Vector3(0f, 0f, 1f), hw));
                list.Add(new SpineSample(new Vector3(0.5f, 0f, 1f), new Vector3(0f, 0f, 1f), hw));
                AppendArc(list, new Vector2(1f, 1f), 0.5f, Mathf.PI, Mathf.PI * 0.5f, segments, hw, skipFirst: true);
                return list;
            }
            return null;
        }

        private static void AppendArc(
            List<SpineSample> list, Vector2 center, float radius,
            float theta0, float theta1, int segments, float halfWidth, bool skipFirst = false)
        {
            float dirSign = Mathf.Sign(theta1 - theta0);
            for (int s = 0; s <= segments; s++)
            {
                if (s == 0 && skipFirst) continue;
                float t = (float)s / segments;
                float theta = Mathf.Lerp(theta0, theta1, t);
                float cs = Mathf.Cos(theta), sn = Mathf.Sin(theta);
                var p = new Vector3(center.x + cs * radius, 0f, center.y + sn * radius);
                var tan = new Vector3(-sn * dirSign, 0f, cs * dirSign).normalized;
                list.Add(new SpineSample(p, tan, halfWidth));
            }
        }
    }
}
