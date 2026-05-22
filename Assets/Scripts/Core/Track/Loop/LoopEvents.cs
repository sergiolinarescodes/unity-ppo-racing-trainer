namespace UnityPpoRacingTrainer.Core.Track.Loop
{
    public readonly record struct LoopClosedEvent(int LoopId, float TotalLength, int AnchorCount);

    public readonly record struct LoopOpenedEvent(int PreviousLoopId);
}
