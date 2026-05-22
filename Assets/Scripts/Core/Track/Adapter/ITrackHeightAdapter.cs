using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Track
{
    /// <summary>
    /// Returns a Y-offset to add to a vertex at canonical local position
    /// (localXZ.x, ?, localXZ.y). Implementations adapt the track's height to its
    /// surroundings — e.g. snapping to terrain corner heights so a track laid on
    /// a slope follows the terrain under it. The flat adapter returns 0.
    /// </summary>
    public interface ITrackHeightAdapter
    {
        float SampleHeight(TrackPieceDefinition def, Vector2 localXZ);
    }
}
