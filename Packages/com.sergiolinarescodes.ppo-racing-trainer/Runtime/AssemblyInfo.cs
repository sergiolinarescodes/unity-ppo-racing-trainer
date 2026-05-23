using System.Runtime.CompilerServices;

// Edit-mode test assemblies need internal access (RewardShaper, ScenarioEventBus,
// SamplePersonalityForCurrentEpisode, internal stage profiles, etc.).
[assembly: InternalsVisibleTo("SergioLinaresCodes.PpoRacingTrainer.Tests")]
// Trainer-only host assemblies (live in the trainer project's Assets/, not in
// this package) need internal access for the trainer scene's bootstrap +
// Build menu. They are not part of the public package surface.
[assembly: InternalsVisibleTo("AiDriverTrainer.Bootstrap")]
[assembly: InternalsVisibleTo("AiDriverTrainer.Editor")]
