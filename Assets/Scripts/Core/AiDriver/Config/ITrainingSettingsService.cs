namespace UnityPpoRacingTrainer.Core.AiDriver.Config
{
    /// <summary>
    /// Loads + exposes the parsed <see cref="TrainingSettings"/> from
    /// <c>settings.json</c> at the project root. Resolve via Reflex DI;
    /// inject into any service that previously read a hardcoded const.
    ///
    /// Fallback: file missing / malformed JSON / partial fields → baked
    /// defaults declared on the record types. Never throws on load.
    /// </summary>
    public interface ITrainingSettingsService
    {
        TrainingSettings Current { get; }

        /// <summary>Re-read <c>settings.json</c> from disk. Not wired by default —
        /// the trainer reads once at boot. Reserved for future hot-reload.</summary>
        void Reload();
    }
}
