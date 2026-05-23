using System.Runtime.CompilerServices;

// Edit-mode test assemblies need internal access (RewardShaper, ScenarioEventBus,
// SamplePersonalityForCurrentEpisode, internal stage profiles, etc.).
[assembly: InternalsVisibleTo("UnityPpoRacingTrainer.Tests")]
[assembly: InternalsVisibleTo("UnityPpoRacingTrainer.Core.Tests")]
