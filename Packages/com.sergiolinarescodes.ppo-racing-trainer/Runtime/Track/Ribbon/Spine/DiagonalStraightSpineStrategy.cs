using System.Collections.Generic;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Track.Ribbon.Spine
{
    internal sealed class DiagonalStraightSpineStrategy : ITrackSpineSampler
    {
        public TrackPieceFamily Family => TrackPieceFamily.DiagonalStraight;

        public IReadOnlyList<SpineSample> Sample(TrackPieceDefinition def)
        {
            if (def.Dimensions.Width != 1) return null;

            // Corner-to-corner along SW → NE.
            const float k = 0.7071067811865475f; // 1/√2
            var tan = new Vector3(k, 0f, k);
            float hw = TrackPieceConstants.LaneHalfWidth;
            return new[]
            {
                new SpineSample(new Vector3(0f, 0f, 0f), tan, hw),
                new SpineSample(new Vector3(1f, 0f, 1f), tan, hw)
            };
        }
    }
}
