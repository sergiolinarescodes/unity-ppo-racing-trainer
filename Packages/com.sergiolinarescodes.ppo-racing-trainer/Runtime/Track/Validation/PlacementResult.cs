namespace UnityPpoRacingTrainer.Core.Track
{
    /// <summary>
    /// Outcome of <see cref="ITrackPlacementService.TryPlace"/>. On success holds the
    /// new piece's id; on failure holds the rejection reason from the first failing
    /// validator in the pipeline.
    /// </summary>
    public readonly record struct PlacementResult(bool Success, TrackPieceId Id, string Reason)
    {
        public static PlacementResult Ok(TrackPieceId id) => new(true, id, null);
        public static PlacementResult Rejected(string reason) => new(false, default, reason);
    }
}
