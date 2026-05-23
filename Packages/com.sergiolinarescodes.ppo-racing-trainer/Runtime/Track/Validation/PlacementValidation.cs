namespace UnityPpoRacingTrainer.Core.Track
{
    /// <summary>
    /// Outcome of one validator run. <see cref="IsValid"/> = false means the
    /// placement should be rejected and <see cref="Reason"/> reported back.
    /// </summary>
    public readonly record struct PlacementValidation(bool IsValid, string Reason)
    {
        public static PlacementValidation Valid => new(true, null);
        public static PlacementValidation Invalid(string reason) => new(false, reason);
    }
}
