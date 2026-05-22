using UnityPpoRacingTrainer.Core.AiDriver.Physics;
using UnityPpoRacingTrainer.Core.AiDriver.Policy;
using UnityPpoRacingTrainer.Core.AiDriver.Training;
using UnityPpoRacingTrainer.Core.AiDriver.Training.Stages;
using UnityPpoRacingTrainer.Core.AiDriver.Versions.Manifest;

namespace UnityPpoRacingTrainer.Core.AiDriver.Versions.V1
{
    /// <summary>
    /// First frozen snapshot of the canonical AI driver profile. Mirrors
    /// <c>LatestVersionProfile</c> field-for-field at the moment of the snapshot
    /// — observation layout, stage curriculum, and physics paths are all locked.
    ///
    /// V1 is the seed of the snapshot pattern. When the canonical evolves and
    /// you want to keep the previous canonical runnable for regression, fork a
    /// sibling <c>Versions/V2/</c> by following <c>docs/snapshot-version.md</c>.
    ///
    /// The reward shaper is still resolved through DI (same <see cref="System.Func{T}"/>
    /// indirection the Latest profile uses). When the canonical
    /// <c>RewardShaper.cs</c> is about to change in a way that materially shifts
    /// what the policy learns, fork it into <c>Versions/V1/V1RewardShaper.cs</c>
    /// at that point and wire it here instead.
    ///
    /// Code-half complete. Editor-half (prefab + ONNX + yaml duplicates) is
    /// noted under <c>PrefabResourcePath</c> / <c>OnnxResourcePath</c> — those
    /// paths are placeholders until someone clicks through the recipe in Unity.
    /// </summary>
    internal sealed class V1VersionProfile : IAiDriverVersionProfile
    {
        // Same lazy-resolution pattern as LatestVersionProfile to break the
        // DI cycle (profile → shaper → active stage profile → profile).
        private readonly System.Func<IRewardShaper> _rewardShaperFactory;
        private readonly StageProfileRegistry _stageProfiles;

        public V1VersionProfile(System.Func<IRewardShaper> rewardShaperFactory)
        {
            _rewardShaperFactory = rewardShaperFactory
                ?? throw new System.ArgumentNullException(nameof(rewardShaperFactory));
            _stageProfiles = new StageProfileRegistry();
            _stageProfiles.Register(0, new Stage0SoloWarmupProfile());
            _stageProfiles.Register(1, new Stage1GridProfile());
            _stageProfiles.Register(2, new Stage2FuelScarcityProfile());
            _stageProfiles.Register(3, new Stage3TireFuelProfile());
            _stageProfiles.Register(4, new Stage4AuthoredTwoCarProfile());
            _stageProfiles.Register(5, new Stage5PackSelfPlayProfile());
        }

        public AiDriverVersion Version => AiDriverVersion.V1;
        public string BehaviorName => "RacingDriverV1";

        // Placeholder. Once the recipe in docs/snapshot-version.md is followed,
        // a duplicated prefab lives at Assets/Resources/AiDriver/Legacy/AiDriverAgentV1.prefab
        // and this path resolves. Until then, picking V1 in TrainerBootstrap
        // surfaces a missing-prefab error at SpawnAgent.
        public string PrefabResourcePath => "AiDriver/Legacy/AiDriverAgentV1";

        // Same placeholder story — paired ONNX must be copied under this name
        // for inference to load.
        public string OnnxResourcePath => "AiDriver/Policies/RacingDriver-v1";

        public string YamlConfigPath => "Assets/_Bootstrap/Configs/MlAgents/racing_driver_v1.yaml";
        public int FloatsPerFrame => RacingObservationLayout.FloatsPerFrame;
        public CarParameters PhysicsDefaults => V1PhysicsProfile.PhysicsDefaults;
        // Frozen V1 snapshot took the same drafting constants as the canonical
        // baseline. If a future canonical mutates DraftingSettings defaults,
        // V1 must keep these historical values explicitly to honor the snapshot
        // contract — initialize fields literal-by-literal here rather than
        // relying on init defaults.
        public DraftingSettings Drafting => new();
        public IRewardShaper RewardShaper => _rewardShaperFactory();
        public bool RequiresSideSystems => true;
        public IStageProfileRegistry StageProfiles => _stageProfiles;
    }
}
