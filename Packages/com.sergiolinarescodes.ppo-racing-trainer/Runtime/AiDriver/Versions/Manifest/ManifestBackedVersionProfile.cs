using System;
using UnityPpoRacingTrainer.Core.AiDriver.Config;
using UnityPpoRacingTrainer.Core.AiDriver.Physics;
using UnityPpoRacingTrainer.Core.AiDriver.Training;
using UnityPpoRacingTrainer.Core.Track;

namespace UnityPpoRacingTrainer.Core.AiDriver.Versions.Manifest
{
    /// <summary>
    /// Data-driven <see cref="IAiDriverVersionProfile"/> backed by a
    /// <see cref="VersionManifest"/>. Reads physics, observation, runtime,
    /// and drafting data straight from the manifest; resolves the reward
    /// shaper through a lazy <c>Func</c> to dodge the DI cycle.
    ///
    /// One instance per known version id is created by
    /// <c>AiDriverVersionsSystemInstaller</c> at bootstrap; the registry
    /// picks the active one by id.
    /// </summary>
    internal sealed class ManifestBackedVersionProfile : IAiDriverVersionProfile
    {
        private readonly VersionManifest _manifest;
        private readonly Func<IRewardShaper> _rewardShaperFactory;
        private readonly CarParameters _physics;

        public ManifestBackedVersionProfile(
            VersionManifest manifest,
            Func<IRewardShaper> rewardShaperFactory)
        {
            _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
            _rewardShaperFactory = rewardShaperFactory ?? throw new ArgumentNullException(nameof(rewardShaperFactory));
            _physics = BuildPhysics(manifest.Physics);
        }

        public string VersionId => _manifest.VersionId;
        public string BehaviorName => _manifest.MlAgents.BehaviorName;
        public string PrefabResourcePath => _manifest.Runtime.PrefabResourcePath;
        public string OnnxResourcePath => _manifest.Runtime.OnnxResourcePath;
        public string YamlConfigPath => _manifest.MlAgents.ConfigPath;
        public int FloatsPerFrame => _manifest.Observation.FloatsPerFrame;
        public CarParameters PhysicsDefaults => _physics;
        public DraftingSettings Drafting => _manifest.Drafting;
        public IRewardShaper RewardShaper => _rewardShaperFactory();
        public bool RequiresSideSystems => _manifest.Runtime.RequiresSideSystems;

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

    }
}
