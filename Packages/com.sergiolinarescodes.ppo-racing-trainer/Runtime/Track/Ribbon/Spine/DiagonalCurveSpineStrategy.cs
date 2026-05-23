using System.Collections.Generic;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Track.Ribbon.Spine
{
    /// <summary>
    /// Cardinal-to-diagonal transition. Cubic Hermite from south-mid-edge (tangent +Z)
    /// to NE corner (tangent NE). MirrorX flips this in the extractor's world transform
    /// to produce the opposite-handed (south → NW corner) variant.
    /// </summary>
    internal sealed class DiagonalCurveSpineStrategy : ITrackSpineSampler
    {
        public TrackPieceFamily Family => TrackPieceFamily.DiagonalCurve;

        public IReadOnlyList<SpineSample> Sample(TrackPieceDefinition def)
        {
            if (def.Dimensions.Width != 1) return null;

            const float k = 0.7071067811865475f;
            var p0 = new Vector3(0.5f, 0f, 0f);
            var p1 = new Vector3(1f, 0f, 1f);
            var m0 = new Vector3(0f, 0f, 1f);
            var m1 = new Vector3(k, 0f, k);
            int segments = TrackPieceConstants.DefaultArcSegments;
            float hw = TrackPieceConstants.LaneHalfWidth;

            var list = new List<SpineSample>(segments + 1);
            for (int s = 0; s <= segments; s++)
            {
                float t = (float)s / segments;
                float h00 = 2f * t * t * t - 3f * t * t + 1f;
                float h10 = t * t * t - 2f * t * t + t;
                float h01 = -2f * t * t * t + 3f * t * t;
                float h11 = t * t * t - t * t;
                Vector3 p = h00 * p0 + h10 * m0 + h01 * p1 + h11 * m1;
                float dh00 = 6f * t * t - 6f * t;
                float dh10 = 3f * t * t - 4f * t + 1f;
                float dh01 = -6f * t * t + 6f * t;
                float dh11 = 3f * t * t - 2f * t;
                Vector3 d = dh00 * p0 + dh10 * m0 + dh01 * p1 + dh11 * m1;
                if (d.sqrMagnitude < 1e-8f) d = m0;
                list.Add(new SpineSample(p, d.normalized, hw));
            }
            return list;
        }
    }
}
