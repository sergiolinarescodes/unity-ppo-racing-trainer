using UnityPpoRacingTrainer.Core.AiDriver.Physics;
using UnityPpoRacingTrainer.Core.Track;

namespace UnityPpoRacingTrainer.Core.AiDriver.Versions.V1
{
    /// <summary>
    /// First frozen physics snapshot. Values are LITERAL-COPIED from
    /// <see cref="AiDriverPhysicsDefaults.Latest"/> at the time of the snapshot.
    /// Edits to <c>AiDriverPhysicsDefaults.Latest</c> will diverge from this
    /// snapshot — that is the whole point. Do not refactor this file to
    /// delegate to the canonical accessor; the snapshot's job is to remain
    /// stable while the canonical evolves.
    ///
    /// To freeze the next snapshot (V2), copy this folder, bump the namespace +
    /// class names, and re-copy <c>AiDriverPhysicsDefaults.Latest</c> at that
    /// point in time. See <c>docs/snapshot-version.md</c>.
    /// </summary>
    internal static class V1PhysicsProfile
    {
        public static CarParameters PhysicsDefaults
        {
            get
            {
                float c = TrackPieceConstants.CarPhysicsCellSize;
                return new CarParameters(
                    WheelBase: 0.3f * c,
                    MaxSteer: 0.45f,
                    MaxAccel: 1.74f * c,
                    MaxBrake: 2.0f * c,
                    MaxSpeed: 7.5f * c,
                    DragCoefficient: 0.5f,
                    SteerRate: 1.2f,
                    Gravity: 9.81f,
                    BoostThrust: 6f * c,
                    BoostDurationSec: 1.5f,
                    BoostRechargeRate: 0.2f,
                    OffTrackDragMul: 5f,
                    OffTrackSpeedCapFrac: 0.30f,
                    LateralGripFactor: 21f,
                    OffTrackGripFactor: 6f,
                    SlipReleaseFactor: 0.25f,
                    SpeedInducedUndersteerGain: 20.0f,
                    MinCruiseSpeed: 0f,
                    LowSpeedTurnBonus: 1.0f,
                    KerbGripFactor: 45f,
                    WallBounceDamping: 0.10f,
                    WallNormalRestitution: 0.15f,
                    CarCollisionRadius: 0.09f * c,
                    OffKerbCorneringPenalty: 0.30f,
                    OffKerbCorneringSteerThreshold: 0.20f,
                    WallStunSeconds: 0.1f,
                    WallDamageCoefficient: 0.08f,
                    WallDamageMinPerHit: 0.02f,
                    MinHealthSpeedFactor: 0.30f,
                    WallStunSecondsPerImpactSpeed: 0.04f,
                    MaxStunSeconds: 0.6f,
                    TractionCircleGain: 8.0f,
                    MinSteerAuthority: 0.18f,
                    HighSpeedSteerRateFactor: 3.5f,
                    SpeedLateralGripScale: 14.0f,
                    TrailBrakeAuthorityWeight: 0.15f,
                    StraightLineAeroBoost: 1.60f,
                    StraightLineAeroRampSec: 1.2f,
                    StraightLineAeroRecoverySec: 0.3f,
                    StraightLineAeroSteerThreshold: 0.10f);
            }
        }
    }
}
