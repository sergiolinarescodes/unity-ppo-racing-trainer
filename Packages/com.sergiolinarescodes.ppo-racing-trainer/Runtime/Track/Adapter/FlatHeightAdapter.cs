using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Track
{
    /// <summary>v1 adapter — track sits at a fixed y. Returns 0 for every vertex.</summary>
    internal sealed class FlatHeightAdapter : ITrackHeightAdapter
    {
        public float SampleHeight(TrackPieceDefinition def, Vector2 localXZ) => 0f;
    }
}
