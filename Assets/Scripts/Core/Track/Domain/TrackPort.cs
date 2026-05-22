namespace UnityPpoRacingTrainer.Core.Track
{
    /// <summary>
    /// One connector on a piece's perimeter, in canonical (north-facing) orientation.
    /// LaneOffset indexes lanes along the side: 0 = leftmost lane (looking outward),
    /// 1 = next, etc. For wide pieces, a single side carries multiple ports.
    /// </summary>
    public readonly record struct TrackPort(TrackDirection Side, int LaneOffset, TrackPortState State);
}
