namespace UnityPpoRacingTrainer.Core.Track.Loop
{
    /// <summary>
    /// Detects when the placed pieces form a closed loop. Watches placement events
    /// and rebuilds the loop on every change. The downstream race / AI / betting
    /// systems consume <see cref="LoopClosedEvent"/> / <see cref="LoopOpenedEvent"/>
    /// rather than polling.
    /// </summary>
    public interface IClosedLoopService
    {
        bool IsLoopClosed { get; }

        bool TryGetCurrentLoop(out ClosedLoop loop);
    }
}
