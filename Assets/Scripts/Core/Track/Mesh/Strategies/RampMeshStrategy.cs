using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Track
{
    /// <summary>
    /// Constant-thickness slab that rises from y=SlabBaseY at the south edge to
    /// y=SlabBaseY+RampRise at the north edge. The slab top is parallel to the
    /// bottom (constant 0.08u thickness) so the road reads as a slope not a wedge.
    /// </summary>
    internal sealed class RampMeshStrategy : ITrackShapeMeshStrategy
    {
        public TrackPieceFamily Family => TrackPieceFamily.Ramp;

        public void Build(MeshBuffer buf, TrackPieceDefinition def, TrackPalette palette)
        {
            int L = def.Dimensions.Length;
            float h = TrackPieceConstants.LaneHalfWidth;
            float rise = TrackPieceConstants.RampRise * L; // rises one step per tile of length

            float yBottomLow = TrackPieceConstants.SlabBaseY;
            float yBottomHigh = yBottomLow + rise;
            float yTopLow = TrackPieceConstants.SlabTopY;
            float yTopHigh = yTopLow + rise;

            MeshPrimitives.AddSlopedSlab(buf,
                0.5f - h, 0.5f + h, 0f, L,
                yBottomLow, yBottomHigh,
                yTopLow, yTopHigh,
                palette.Road, palette.RampStripe, palette.RoadEdge);
        }
    }
}
