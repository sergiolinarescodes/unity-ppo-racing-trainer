namespace UnityPpoRacingTrainer.Core.AiDriver.Training.Generation
{
    /// <summary>
    /// Procedural training-track generator. Walks the grid placing pieces via
    /// <c>ITrackPlacementService</c> until <c>IClosedLoopService</c> reports a
    /// closed loop, or the stage's piece cap is hit.
    /// </summary>
    public interface IProceduralLoopGenerator
    {
        GenerationResult Generate(in GenerationConfig cfg);
    }
}
