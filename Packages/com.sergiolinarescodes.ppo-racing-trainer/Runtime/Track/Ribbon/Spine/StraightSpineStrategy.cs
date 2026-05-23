using System.Collections.Generic;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Track.Ribbon.Spine
{
    internal sealed class StraightSpineStrategy : ITrackSpineSampler
    {
        public TrackPieceFamily Family => TrackPieceFamily.Straight;

        public IReadOnlyList<SpineSample> Sample(TrackPieceDefinition def)
        {
            // Single-lane straights only. Wide straights (W=2) are filtered upstream;
            // they don't participate in the smoothed ribbon — per-piece slabs render them.
            if (def.Dimensions.Width != 1) return null;
            int L = def.Dimensions.Length;
            float hw = TrackPieceConstants.LaneHalfWidth;
            return new[]
            {
                new SpineSample(new Vector3(0.5f, 0f, 0f), new Vector3(0f, 0f, 1f), hw),
                new SpineSample(new Vector3(0.5f, 0f, L), new Vector3(0f, 0f, 1f), hw)
            };
        }
    }
}
