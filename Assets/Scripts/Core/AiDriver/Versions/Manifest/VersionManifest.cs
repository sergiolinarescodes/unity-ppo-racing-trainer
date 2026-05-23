using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.AiDriver.Config;

namespace UnityPpoRacingTrainer.Core.AiDriver.Versions.Manifest
{

    /// <summary>
    /// Self-contained per-version JSON manifest. One file per version under
    /// <c>Assets/_Bootstrap/Configs/Versions/&lt;version_id&gt;.json</c>.
    /// The single source of truth for every per-version tunable: physics
    /// constants, drafting, tire wear, reward weights, observation layout
    /// selection, prefab / ONNX paths.
    ///
    /// Adding a new version is one step: drop a new <c>&lt;id&gt;.json</c>
    /// next to <c>latest.json</c>. The trainer auto-registers every
    /// manifest file under <c>Configs/Versions/</c>; switch the active id
    /// via <c>TrainerBootstrap.activeVersionId</c>. The dashboard at
    /// <c>http://localhost:8765/settings?version=&lt;id&gt;</c> edits the
    /// non-frozen sections of this file in place; snapshots (anything except
    /// <c>"latest"</c>) reject writes by default to keep them paired with
    /// their ONNX.
    ///
    /// Reuses the field shapes from <see cref="TrainingSettings"/> so the
    /// existing JSON round-trip patterns continue to apply.
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

        public CodeModulesSection CodeModules { get; init; } = new();

        /// <summary>
        /// Optional list of extra reward channels (in addition to the
        /// monolithic <c>RewardShaper</c>). Each entry's <c>Id</c> must resolve
        /// in <c>IRewardChannelRegistry</c>. This is the extension point for
        /// "add a new reward signal to a future version without touching prior
        /// frozen snapshots": snapshots that don't list the channel id stay
        /// bit-identical.
        /// </summary>
        public IReadOnlyList<RewardChannelManifestEntry> RewardChannels { get; init; } = Array.Empty<RewardChannelManifestEntry>();
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
        // Assets/Resources/AiDriver/Policies/" — used by latest.json so the
        // skill doesn't need to bump this string after every model swap.
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

    /// <summary>
    /// One row in <see cref="VersionManifest.RewardChannels"/>. The
    /// <c>Id</c> selects a registered <c>IRewardChannel</c>.
    /// </summary>
    public sealed record RewardChannelManifestEntry
    {
        public string Id { get; init; } = "";
    }
}
