namespace UnityPpoRacingTrainer.Core.Track.Topology
{
    /// <summary>
    /// Fires only when the open-port set or closure status changes. Subscribers
    /// (notably the ghost-loop director) query <see cref="ITrackEndingService"/>
    /// for the live values — this struct carries the deduped change signal only.
    /// </summary>
    public readonly record struct TrackTopologyChangedEvent(int OpenEndCount, bool IsClosedLoop);
}
