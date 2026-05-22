using System;
using UnityPpoRacingTrainer.Core.AiDriver.Config;
using UnityPpoRacingTrainer.Core.AiDriver.Physics;
using UnityPpoRacingTrainer.Core.AiDriver.Training;
using UnityPpoRacingTrainer.Core.AiDriver.Training.Stages;
using UnityPpoRacingTrainer.Core.Track;

namespace UnityPpoRacingTrainer.Core.AiDriver.Versions.Manifest
{
    /// <summary>
    /// Data-driven <see cref="IAiDriverVersionProfile"/> backed by a
    /// <see cref="VersionManifest"/>. Reads physics, observation, runtime,
    /// and stage data from the manifest; resolves the reward shaper through
    /// the same lazy-<c>Func</c> indirection the C# profiles use to avoid
    /// the DI cycle.
    ///
    /// Phase 1: this class exists but no installer binds it as the active
    /// profile. The current <c>LatestVersionProfile</c> remains authoritative.
    /// </summary>
    internal sealed class ManifestBackedVersionProfile : IAiDriverVersionProfile
    {
        private readonly VersionManifest _manifest;
        private readonly Func<IRewardShaper> _rewardShaperFactory;
        private readonly StageProfileRegistry _stageProfiles;
        private readonly CarParameters _physics;
        private readonly AiDriverVersion _versionEnum;

        public ManifestBackedVersionProfile(
            VersionManifest manifest,
            Func<IRewardShaper> rewardShaperFactory,
            AiDriverVersion versionEnum)
        {
            _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
            _rewardShaperFactory = rewardShaperFactory ?? throw new ArgumentNullException(nameof(rewardShaperFactory));
            _versionEnum = versionEnum;
            _physics = BuildPhysics(manifest.Physics);
            _stageProfiles = BuildStages(manifest.Stages);
        }

        public AiDriverVersion Version => _versionEnum;
        public string BehaviorName => _manifest.MlAgents.BehaviorName;
        public string PrefabResourcePath => _manifest.Runtime.PrefabResourcePath;
        public string OnnxResourcePath => _manifest.Runtime.OnnxResourcePath;
        public string YamlConfigPath => _manifest.MlAgents.ConfigPath;
        public int FloatsPerFrame => _manifest.Observation.FloatsPerFrame;
        public CarParameters PhysicsDefaults => _physics;
        public DraftingSettings Drafting => _manifest.Drafting;
        public IRewardShaper RewardShaper => _rewardShaperFactory();
        public bool RequiresSideSystems => _manifest.Runtime.RequiresSideSystems;
        public IStageProfileRegistry StageProfiles => _stageProfiles;

        public VersionManifest Manifest => _manifest;

        // Mirrors AiDriverPhysicsDefaults.Latest field-for-field. The same
        // cell-size scaling rules apply: linear quantities multiply by c,
        // angles/time-rates/drag stay scale-invariant. Keep in sync if
        // CarParameters or the scaling convention changes.
        private static CarParameters BuildPhysics(PhysicsSettings p)
        {
            float c = TrackPieceConstants.CarPhysicsCellSize;
            return new CarParameters(
                WheelBase: p.WheelBase * c,
                MaxSteer: p.MaxSteer,
                MaxAccel: p.MaxAccel * c,
                MaxBrake: p.MaxBrake * c,
                MaxSpeed: p.MaxSpeed * c,
                DragCoefficient: p.DragCoefficient,
                SteerRate: p.SteerRate,
                Gravity: p.Gravity,
                BoostThrust: p.BoostThrust * c,
                BoostDurationSec: p.BoostDurationSec,
                BoostRechargeRate: p.BoostRechargeRate,
                OffTrackDragMul: p.OffTrackDragMul,
                OffTrackSpeedCapFrac: p.OffTrackSpeedCapFrac,
                LateralGripFactor: p.LateralGripFactor,
                OffTrackGripFactor: p.OffTrackGripFactor,
                SpeedInducedUndersteerGain: p.SpeedInducedUndersteerGain,
                SlipReleaseFactor: p.SlipReleaseFactor,
                MinCruiseSpeed: p.MinCruiseSpeed,
                LowSpeedTurnBonus: p.LowSpeedTurnBonus,
                KerbGripFactor: p.KerbGripFactor,
                WallBounceDamping: p.WallBounceDamping,
                WallNormalRestitution: p.WallNormalRestitution,
                CarCollisionRadius: p.CarCollisionRadius * c,
                OffKerbCorneringPenalty: p.OffKerbCorneringPenalty,
                OffKerbCorneringSteerThreshold: p.OffKerbCorneringSteerThreshold,
                WallStunSeconds: p.WallStunSeconds,
                WallDamageCoefficient: p.WallDamageCoefficient,
                WallDamageMinPerHit: p.WallDamageMinPerHit,
                MinHealthSpeedFactor: p.MinHealthSpeedFactor,
                WallStunSecondsPerImpactSpeed: p.WallStunSecondsPerImpactSpeed,
                MaxStunSeconds: p.MaxStunSeconds,
                TractionCircleGain: p.TractionCircleGain,
                MinSteerAuthority: p.MinSteerAuthority,
                HighSpeedSteerRateFactor: p.HighSpeedSteerRateFactor,
                SpeedLateralGripScale: p.SpeedLateralGripScale,
                TrailBrakeAuthorityWeight: p.TrailBrakeAuthorityWeight,
                StraightLineAeroBoost: p.StraightLineAeroBoost,
                StraightLineAeroRampSec: p.StraightLineAeroRampSec,
                StraightLineAeroRecoverySec: p.StraightLineAeroRecoverySec,
                StraightLineAeroSteerThreshold: p.StraightLineAeroSteerThreshold);
        }

        private static StageProfileRegistry BuildStages(System.Collections.Generic.IReadOnlyList<StageManifestEntry> stages)
        {
            var reg = new StageProfileRegistry();
            foreach (var entry in stages)
            {
                reg.Register(entry.Id, new ManifestStageProfile(entry));
            }
            return reg;
        }
    }

    /// <summary>
    /// Manifest-driven <see cref="IStageProfile"/>. Replaces the 6 hand-written
    /// classes in <c>StageProfiles.cs</c> once Phase 4 deletes them; in Phase 1
    /// it is reachable only through <see cref="ManifestBackedVersionProfile"/>
    /// (which is itself dormant).
    /// </summary>
    internal sealed class ManifestStageProfile : IStageProfile
    {
        private readonly StageManifestEntry _entry;
        private readonly StageFeature _features;
        private readonly FuelSamplingMode _fuel;
        private readonly PersonalitySamplingMode _personality;

        public ManifestStageProfile(StageManifestEntry entry)
        {
            _entry = entry ?? throw new ArgumentNullException(nameof(entry));
            _features = StageFeatureCatalog.Parse(entry.Features);
            _fuel = StageFeatureCatalog.ParseFuel(entry.FuelSampling);
            _personality = StageFeatureCatalog.ParsePersonality(entry.PersonalitySampling);
        }

        public int StageId => _entry.Id;
        public string Name => _entry.Name;
        public StageFeature Features => _features;
        public int ExpectedOpponentCount => _entry.ExpectedOpponentCount;
        public FuelSamplingMode Fuel => _fuel;
        public PersonalitySamplingMode Personality => _personality;
    }
}
