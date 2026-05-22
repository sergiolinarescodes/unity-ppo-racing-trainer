namespace UnityPpoRacingTrainer.Core.AiDriver.Training.Generation
{
    /// <summary>
    /// Outcome of one generation attempt. <see cref="Success"/> = false means the
    /// caller should retry with a fresh seed (or a different stage). On success,
    /// <see cref="LoopId"/> matches the id published in <c>LoopClosedEvent</c>.
    /// </summary>
    public readonly record struct GenerationResult(
        bool Success,
        int LoopId,
        int PlacedPieces,
        float TotalLength,
        string FailureReason)
    {
        public static GenerationResult Ok(int loopId, int placedPieces, float totalLength)
            => new(true, loopId, placedPieces, totalLength, null);

        public static GenerationResult Failed(string reason)
            => new(false, 0, 0, 0f, reason);
    }
}
