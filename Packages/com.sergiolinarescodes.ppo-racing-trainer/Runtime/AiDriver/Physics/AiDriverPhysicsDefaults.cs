using UnityPpoRacingTrainer.Core.Track;

namespace UnityPpoRacingTrainer.Core.AiDriver.Physics
{
    /// <summary>
    /// Canonical static accessor for the AI driver's physics defaults. Replaces
    /// the now-deleted <c>CarParameters.Default</c> — call sites must read from
    /// <see cref="Latest"/> explicitly.
    ///
    /// Why split this from <see cref="IAiDriverVersionProfile"/>: tests + debug
    /// renderers that don't go through DI still need a baseline; keeping these
    /// as plain statics avoids forcing every call site to resolve a profile
    /// from a container.
    /// </summary>
    public static class AiDriverPhysicsDefaults
    {
        /// <summary>
        /// Canonical physics tunings. The trade-off the policy must learn:
        /// long straights with real top-speed gaps + heavy braking zones,
        /// strong speed-induced understeer (must brake before turning, not
        /// after), wheel-response collapse near top speed (flick-saves
        /// rare), and lateral-grip falloff at speed so sweepers force lifts
        /// and hairpins force heavy brakes. The combination keeps low-speed
        /// circuit shape (curve angle, kerb usage) on the lap-time gradient
        /// instead of letting the policy "send it" through everything.
        /// </summary>
        public static CarParameters Latest
        {
            get
            {
                float c = TrackPieceConstants.CarPhysicsCellSize;
                return new CarParameters(
                    WheelBase: 0.3f * c,
                    MaxSteer: 0.45f,
                    MaxAccel: 1.74f * c,
                    MaxBrake: 2.0f * c,
                    // 7.5·c chosen so straights bite hard, braking zones
                    // extend, and the mid-corner-to-top-speed gap is wide.
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
