namespace UnityPpoRacingTrainer.Core.Track
{
    /// <summary>
    /// State of a single connector on a piece's tile-edge. Wide pieces have multiple
    /// ports per side, indexed by lane offset.
    /// </summary>
    public enum TrackPortState : byte
    {
        None = 0,
        Road,
        RoadElevatedLow,
        RoadElevatedHigh
    }
}
