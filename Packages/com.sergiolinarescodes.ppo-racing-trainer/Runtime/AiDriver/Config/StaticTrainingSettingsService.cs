namespace UnityPpoRacingTrainer.Core.AiDriver.Config
{
    /// <summary>
    /// In-memory <see cref="ITrainingSettingsService"/> that returns the
    /// supplied <see cref="TrainingSettings"/> instance unchanged. Use for
    /// scenario tests where the disk-loading <see cref="TrainingSettingsService"/>
    /// would require a Unity runtime / file system. No-arg form returns the
    /// baked defaults.
    /// </summary>
    public sealed class StaticTrainingSettingsService : ITrainingSettingsService
    {
        public TrainingSettings Current { get; }

        public StaticTrainingSettingsService() : this(new TrainingSettings()) { }

        public StaticTrainingSettingsService(TrainingSettings settings)
        {
            Current = settings ?? new TrainingSettings();
        }

        public void Reload() { /* static */ }
    }
}
