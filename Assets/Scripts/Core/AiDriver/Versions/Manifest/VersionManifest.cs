using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.AiDriver.Config;

namespace UnityPpoRacingTrainer.Core.AiDriver.Versions.Manifest
{
    /// <summary>
    /// Self-contained per-version JSON manifest. One file per version under
    /// <c>Assets/_Bootstrap/Configs/Versions/&lt;version_id&gt;.json</c>.
    ///
    /// Phase 1: dormant — loader + POCO exist but no DI binding consumes this
    /// as the active <see cref="IAiDriverVersionProfile"/>. The current C#
    /// <c>LatestVersionProfile</c> / <c>V1VersionProfile</c> remain authoritative
    /// until later phases flip the switch (see plan
    /// <c>~/.claude/plans/can-you-analyze-how-floofy-gadget.md</c>).
    ///
    /// Reuses the field shapes from <see cref="TrainingSettings"/> so the
    /// dashboard's existing JSON round-trip patterns continue to apply.
    /// </summary>
    public sealed record VersionManifest
    {
        public int SchemaVersion { get; init; } = 1;
        public string VersionId { get; init; } = "latest";
        public string DisplayName { get; init; } = "Latest";

        public MlAgentsSection MlAgents { get; init; } = new();
        public RuntimeSection Runtime { get; init; } = new();

        public EpisodeSettings Episode { get; init; } = new();
        public PhysicsSettings Physics { get; init; } = new();
        public TirePhysicsSettings TirePhysics { get; init; } = new();
        public DraftingSettings Drafting { get; init; } = new();
        public RewardShaperSettings RewardShaper { get; init; } = new();
        public TrackGeometrySettings TrackGeometry { get; init; } = new();
        public ObservationSettings Observation { get; init; } = new();

        public IReadOnlyList<StageManifestEntry> Stages { get; init; } = Array.Empty<StageManifestEntry>();

        public CodeModulesSection CodeModules { get; init; } = new();
    }

    public sealed record MlAgentsSection
    {
        public string BehaviorName { get; init; } = "RacingDriver";
        public string ConfigPath { get; init; } = "Assets/_Bootstrap/Configs/MlAgents/racing_driver.yaml";
    }

    public sealed record RuntimeSection
    {
        public string PrefabResourcePath { get; init; } = "AiDriver/AiDriverAgent";

        // The sentinel "latest" means "newest-mtime RacingDriver-*.onnx under
        // Assets/Resources/AiDriver/Policies/" — same semantics as the C#
        // LatestVersionProfile's OnnxResourcePath.
        public string OnnxResourcePath { get; init; } = "latest";

        public bool RequiresSideSystems { get; init; } = true;
    }

    /// <summary>
    /// Baked-constant section in <c>Physics/Draft/Drafting.cs</c>. Phase 2
    /// pulls these out of the C# file into the manifest; in Phase 1 they are
    /// already represented here so the schema is complete.
    /// </summary>
    public sealed record DraftingSettings
    {
        public float MaxDistance { get; init; } = 8f;
        public float LateralTolerance { get; init; } = 2.5f;
        public float DragReduction { get; init; } = 0.33f;
        public float AccelBoost { get; init; } = 0.09625f;
        public float MinActivationMS { get; init; } = 6f;
        public float LaunchGraceSec { get; init; } = 3f;
        public float AttackTau { get; init; } = 0.05f;
        public float ReleaseTau { get; init; } = 0.7f;
    }

    /// <summary>
    /// One stage in the curriculum. Mirrors the per-class shape of
    /// <c>StageProfiles.cs</c>; features list maps to <c>StageFeature</c>
    /// enum bits via <c>StageFeatureCatalog</c>.
    /// </summary>
    public sealed record StageManifestEntry
    {
        public int Id { get; init; }
        public string Name { get; init; } = "";
        public IReadOnlyList<string> Features { get; init; } = Array.Empty<string>();
        public int ExpectedOpponentCount { get; init; }
        public string FuelSampling { get; init; } = "abundant";
        public string PersonalitySampling { get; init; } = "uniform";
    }

    /// <summary>
    /// Names of the code-module strategies this version uses. Each id resolves
    /// to an instance in the matching registry (<c>IPhysicsModelRegistry</c>,
    /// <c>IObservationWriterRegistry</c>, <c>IRewardChannelRegistry</c>).
    ///
    /// Adding a new behavior = register a new strategy in code under a new id
    /// + reference that id from a new version's manifest. Old version manifests
    /// don't list the new id, so old versions are unaffected.
    /// </summary>
    public sealed record CodeModulesSection
    {
        public string PhysicsModel { get; init; } = "Default";
        public string ObservationWriter { get; init; } = "RacingV1";
        public string RewardShaper { get; init; } = "Composite";
    }
}
