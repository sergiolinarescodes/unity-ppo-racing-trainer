using UnityPpoRacingTrainer.Core.AiDriver.Physics;
using UnityPpoRacingTrainer.Core.AiDriver.Policy;
using UnityPpoRacingTrainer.Core.AiDriver.Training;
using UnityPpoRacingTrainer.Core.AiDriver.Training.Stages;
using UnityPpoRacingTrainer.Core.AiDriver.Versions.Manifest;

namespace UnityPpoRacingTrainer.Core.AiDriver.Versions.Latest
{
    /// <summary>
    /// Canonical "Latest" version profile — current 60-float observation
    /// schema (<see cref="RacingObservationLayout"/>), canonical physics
    /// from <see cref="AiDriverPhysicsDefaults"/>, full side-system stack.
    /// Maps to BehaviorName <c>RacingDriver</c>
    /// (the Python trainer key the ONNX is trained against; the prefab
    /// and yaml must match this string verbatim).
    ///
    /// Frozen snapshots: when the next canonical version is taken, the
    /// current "Latest" gets a sibling profile under <c>Versions/V<N>/</c>
    /// and registered alongside in <c>AiDriverVersionsSystemInstaller</c>
    /// so prior weights remain runnable for regression. See
    /// <c>docs/snapshot-version.md</c>.
    /// </summary>
    internal sealed class LatestVersionProfile : IAiDriverVersionProfile
    {
        // Lazy reward-shaper resolution breaks the DI cycle
        //   LatestVersionProfile → IRewardShaper → RewardShaper
        //                       → IActiveStageProfile → ActiveStageProfile
        //                       → IAiDriverVersionProfile (back to top)
        // by deferring the .Resolve<IRewardShaper>() call out of the
        // version-profile ctor and into the property getter, where it
        // fires after every installer has registered.
        private readonly System.Func<IRewardShaper> _rewardShaperFactory;
        private readonly StageProfileRegistry _stageProfiles;

        public LatestVersionProfile(System.Func<IRewardShaper> rewardShaperFactory)
        {
            _rewardShaperFactory = rewardShaperFactory ?? throw new System.ArgumentNullException(nameof(rewardShaperFactory));
            _stageProfiles = new StageProfileRegistry();
            _stageProfiles.Register(0, new Stage0SoloWarmupProfile());
            _stageProfiles.Register(1, new Stage1GridProfile());
            _stageProfiles.Register(2, new Stage2FuelScarcityProfile());
            _stageProfiles.Register(3, new Stage3TireFuelProfile());
            _stageProfiles.Register(4, new Stage4AuthoredTwoCarProfile());
            _stageProfiles.Register(5, new Stage5PackSelfPlayProfile());
        }

        public AiDriverVersion Version => AiDriverVersion.Latest;
        public string BehaviorName => "RacingDriver";
        public string PrefabResourcePath => "AiDriver/AiDriverAgent";
        public string OnnxResourcePath => "latest";
        public string YamlConfigPath => "Assets/_Bootstrap/Configs/MlAgents/racing_driver.yaml";
        public int FloatsPerFrame => RacingObservationLayout.FloatsPerFrame;
        public CarParameters PhysicsDefaults => AiDriverPhysicsDefaults.Latest;
        // Defaults on DraftingSettings mirror the historical baked constants
        // in Drafting.cs (8 / 2.5 / 0.33 / 0.09625 / 6 / 3 / 0.05 / 0.7) — keep
        // in sync if either side moves. When Phase 6 deletes this profile, the
        // ManifestBackedVersionProfile fully replaces this path.
        public DraftingSettings Drafting => new();
        public IRewardShaper RewardShaper => _rewardShaperFactory();
        public bool RequiresSideSystems => true;
        public IStageProfileRegistry StageProfiles => _stageProfiles;
    }
}
