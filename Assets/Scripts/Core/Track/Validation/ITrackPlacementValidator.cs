namespace UnityPpoRacingTrainer.Core.Track
{
    /// <summary>
    /// One link in the placement validation pipeline. Implementations must be stateless;
    /// all input flows through <see cref="PlacementContext"/>. Order of execution is the
    /// registration order in <c>TrackSystemInstaller</c>; the first failure short-circuits.
    /// </summary>
    public interface ITrackPlacementValidator
    {
        PlacementValidation Validate(PlacementContext ctx);
    }
}
