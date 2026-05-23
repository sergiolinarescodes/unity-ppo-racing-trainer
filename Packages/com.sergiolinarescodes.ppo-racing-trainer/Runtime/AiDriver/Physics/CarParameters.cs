using UnityPpoRacingTrainer.Core.Track;

namespace UnityPpoRacingTrainer.Core.AiDriver.Physics
{
    /// <summary>
    /// Per-car physical parameters consumed by <see cref="CarSimulationService"/>.
    /// Conventions:
    /// <list type="bullet">
    /// <item>Distances in world units, times in seconds, angles in radians.</item>
    /// <item>Linear quantities (wheelbase, acceleration, brake, top speed, boost
    /// thrust, collision radius) scale with <see cref="TrackPieceConstants.CarPhysicsCellSize"/>
    /// so tile-traversal time stays invariant when the grid is rescaled.</item>
    /// <item>Angles, time-rates, and drag are scale-invariant (no cell-size factor).</item>
    /// </list>
    /// All ranges below assume <c>c = CarPhysicsCellSize</c>.
    /// </summary>
    /// <param name="WheelBase">
    /// Distance between front and rear axle, world units. Drives the kinematic-bicycle
    /// turn radius: <c>R = WheelBase / tan(steer)</c>.
    /// UP: wider turn radius, more stable at speed, harder to fit tight corners.
    /// DOWN: tighter radius, easier hairpins, more twitchy / cantilevered at high speed.
    /// </param>
    /// <param name="MaxSteer">
    /// Maximum steering angle, radians. Hard cap on commanded steer at full lock.
    /// UP: tighter minimum turning radius, more responsive low-speed maneuvering.
    /// DOWN: car physically cannot make sharp turns; forces wider lines.
    /// </param>
    /// <param name="MaxAccel">
    /// Throttle acceleration, m/s² (scaled by cell size). Applied when throttle is positive.
    /// UP: punchier launch, faster corner-exit recovery, easier to overshoot braking points.
    /// DOWN: heavier throttle feel, longer time in each speed regime so the policy
    /// can steer cleanly, slower exits.
    /// </param>
    /// <param name="MaxBrake">
    /// Brake deceleration, m/s² (scaled by cell size). Applied when brake is positive.
    /// UP: shorter braking distance, allows late-entry trail-brake saves.
    /// DOWN: forces earlier braking commitment; cannot bail out of late entries.
    /// </param>
    /// <param name="MaxSpeed">
    /// Hard top-speed cap, m/s (scaled by cell size). Longitudinal velocity magnitude
    /// is clamped to this after drag and aero adjustments.
    /// UP: higher straight-line ceiling, longer braking zones needed before corners.
    /// DOWN: lower top speed, smaller speed differentials between drivers.
    /// </param>
    /// <param name="DragCoefficient">
    /// Air-resistance coefficient (dimensionless). Speed decays at a rate proportional
    /// to <c>DragCoefficient × v</c> when off-throttle.
    /// UP: faster coast-down, shorter natural straights, heavier penalty for lifting.
    /// DOWN: car holds speed longer when coasting; rewards efficient throttle use.
    /// </param>
    /// <param name="SteerRate">
    /// Steering rate-of-change limit, radians per second. Commanded steer cannot move
    /// faster than this between ticks.
    /// UP: snappier wheel response, more nervous handling, easier to over-correct.
    /// DOWN: smoother / lazier wheel, harder to flick into sudden chicanes.
    /// </param>
    /// <param name="Gravity">
    /// Gravitational acceleration constant, m/s². Used for ramp and weight-transfer
    /// terms. Physics constant — do not tune for gameplay.
    /// </param>
    /// <param name="BoostThrust">
    /// Nitro/boost extra thrust, m/s² (scaled by cell size). Adds on top of
    /// <see cref="MaxAccel"/> while boost is active.
    /// UP: bigger overtake punch when boost fires.
    /// DOWN: boost barely felt — more decorative than strategic.
    /// </param>
    /// <param name="BoostDurationSec">
    /// How long one boost activation lasts, seconds.
    /// UP: boost covers more of a straight in a single press.
    /// DOWN: boost becomes a short tap — forces precise timing near corner exits.
    /// </param>
    /// <param name="BoostRechargeRate">
    /// Boost fuel refill rate, fraction of full budget per second.
    /// UP: boost is available more often; reduces strategic scarcity.
    /// DOWN: each boost matters more; forces saving for the longest straight.
    /// </param>
    /// <param name="OffTrackDragMul">
    /// Drag multiplier applied while the projection flags off-track. Effective
    /// off-track drag = <see cref="DragCoefficient"/> × this.
    /// UP: grass kills speed faster — strong incentive to stay on tarmac.
    /// DOWN: lawn-mower shortcuts become viable, undermining the racing line.
    /// </param>
    /// <param name="OffTrackSpeedCapFrac">
    /// Hard top-speed clamp while off-track, as a fraction of <see cref="MaxSpeed"/>.
    /// Off-track velocity is capped to <c>MaxSpeed × this</c>.
    /// UP: grass excursions cost less speed; cars can cut corners.
    /// DOWN: 4-wheels-off cars crawl; off-track is a hard punishment.
    /// </param>
    /// <param name="LateralGripFactor">
    /// On-track lateral-grip rate (1/s). Decays the component of velocity perpendicular
    /// to heading at this rate (time constant ≈ 1/value).
    /// UP: car holds the racing line tighter, drift becomes shorter, lap times improve
    /// on twisty circuits.
    /// DOWN: pronounced slide on aggressive turn-in, looser brake-into-corner recovery,
    /// looks more rally / arcade.
    /// </param>
    /// <param name="OffTrackGripFactor">
    /// Off-track lateral-grip rate (1/s). Same role as <see cref="LateralGripFactor"/>
    /// but on grass / sand.
    /// UP: car still tracks on grass excursions.
    /// DOWN: grass produces visible drift and reduces driver agency off-line.
    /// </param>
    /// <param name="SpeedInducedUndersteerGain">
    /// Speed × steer-demand coupling. Effective steer is divided by
    /// <c>1 + SpeedInducedUndersteerGain × speedFrac² × |steerNorm|</c>, with
    /// <c>speedFrac = currentSpeed / MaxSpeed</c>.
    /// UP: car pushes wide at speed even at full lock — hairpins become a brake-first
    /// commitment; cannot rotate at top speed.
    /// DOWN: pure kinematic steering; the car turns equally well at any speed.
    /// </param>
    /// <param name="SlipReleaseFactor">
    /// Slip-aware grip degradation. Effective grip =
    /// <c>baseGrip × lerp(1, SlipReleaseFactor, slip)</c> where
    /// <c>slip = |lateralVelocity| / speed</c>. 1.0 = no degradation; 0.0 = grip vanishes
    /// at full sideways drift.
    /// UP: tires keep biting even when sliding — self-recovers from over-rotation.
    /// DOWN: once the car is sliding it stays sliding; the policy MUST brake before
    /// entry instead of catching slides mid-corner.
    /// </param>
    /// <param name="MinCruiseSpeed">
    /// Engine-idle floor, m/s. Minimum forward speed sustained on-track even at full brake.
    /// Models a road car creeping in gear at idle.
    /// UP: car cannot fully stop on track; brake bottoms out at a creep.
    /// DOWN (0): brake can bring the car to a dead halt; the policy can park.
    /// </param>
    /// <param name="LowSpeedTurnBonus">
    /// Multiplicative yaw-rate boost at low speed. Effective yaw rate is multiplied by
    /// <c>1 + LowSpeedTurnBonus × (1 − speedFrac)</c>, peaks at standstill and decays
    /// to 1.0 at <see cref="MaxSpeed"/>.
    /// UP: tight low-speed maneuvers rotate faster; compensates for the kinematic-bicycle
    /// "slow cars barely turn" limit.
    /// DOWN (0): pure kinematic yaw — yaw rate is proportional to speed only.
    /// </param>
    /// <param name="KerbGripFactor">
    /// Lateral-grip rate (1/s) while the projection lies on the kerb surface. Bypasses
    /// <see cref="OffKerbCorneringPenalty"/> and <see cref="SpeedLateralGripScale"/>.
    /// UP: setting a wheel on the kerb gives a strong cornering advantage — emergent
    /// racing-line behaviour without reward shaping.
    /// DOWN: kerbs become neutral; no incentive to flirt with the apex.
    /// </param>
    /// <param name="WallBounceDamping">
    /// Speed retained along the wall-parallel axis after a wall hit (0..1).
    /// 0 = full kill, 1 = unaffected.
    /// UP: car keeps more speed after grazing a wall — wall-riding becomes viable.
    /// DOWN: wall contact is brutal — strong incentive to stay off the barriers.
    /// </param>
    /// <param name="WallNormalRestitution">
    /// Wall-normal outbound rebound speed, as a fraction of incoming normal speed (0..1).
    /// UP: car bounces off the wall outward instead of sticking; more visible kick.
    /// DOWN: car stays glued to the wall after impact, scraping along it.
    /// </param>
    /// <param name="CarCollisionRadius">
    /// Car-footprint radius (XZ plane), world units. Used for wall collision queries
    /// and car-car contact tests. Must stay well below the road half-width.
    /// UP: bigger footprint — car clips walls earlier, fewer near-misses.
    /// DOWN: smaller footprint, more clearance, can navigate tighter corridors.
    /// </param>
    /// <param name="OffKerbCorneringPenalty">
    /// Off-kerb cornering destabilization. While off the kerb AND
    /// <c>|steerNorm| &gt; OffKerbCorneringSteerThreshold</c>, lateral grip is scaled by
    /// <c>1 − OffKerbCorneringPenalty × t</c> where <c>t</c> ramps linearly from 0 at
    /// the threshold to 1 at full lock. Kerb path bypasses the rule entirely.
    /// UP: hard cornering off-kerb is punished harder — emergent kerb-hugging.
    /// DOWN: no off-kerb penalty; the smooth racing line is just as fast as the apex.
    /// </param>
    /// <param name="OffKerbCorneringSteerThreshold">
    /// Steering deadband for <see cref="OffKerbCorneringPenalty"/>, normalized steer (0..1).
    /// Below this the penalty does not apply.
    /// UP: only extreme lock loses grip; gentle off-kerb steering stays rigid.
    /// DOWN: even small off-kerb corrections lose grip — encourages micro-adjustments
    /// on the kerb.
    /// </param>
    /// <param name="WallStunSeconds">
    /// Minimum input-lockout duration after a wall hit, seconds. While stunned the car
    /// ignores steer/throttle/brake and coasts under drag.
    /// UP: every wall touch costs more recovery time.
    /// DOWN (0): the policy can power back through the wall instantly.
    /// </param>
    /// <param name="WallDamageCoefficient">
    /// Chassis damage per wall impact: <c>damage = WallDamageCoefficient × impactSpeed²</c>.
    /// Health is clamped [0,1]; at 0 the episode ends with a wreck.
    /// UP: full-speed crashes are catastrophic; encourages careful approach.
    /// DOWN (0): walls cost only speed, never health.
    /// </param>
    /// <param name="WallDamageMinPerHit">
    /// Per-hit damage floor applied AFTER the quadratic
    /// <see cref="WallDamageCoefficient"/> term, so very-low-speed wall
    /// contact (where k·s² ≈ 0) still accrues chassis cost. Closes the
    /// "ride the wall as a guide rail" failure mode where a policy
    /// learns walls are free at scrubbing speed.
    /// UP: any contact eats a meaningful chunk of health; corner kissing punished.
    /// DOWN (0): no floor — only the quadratic term applies (legacy behaviour).
    /// </param>
    /// <param name="MinHealthSpeedFactor">
    /// Top-speed multiplier when health = 0. Effective cap =
    /// <c>MaxSpeed × lerp(MinHealthSpeedFactor, 1, health)</c>.
    /// UP: a wrecked car still moves at decent pace; weak penalty for damage.
    /// DOWN: wreck limps at a crawl — finishing the lap is still possible but the
    /// gradient strongly favours clean driving.
    /// </param>
    /// <param name="WallStunSecondsPerImpactSpeed">
    /// Per-(m/s) impact-speed stun coefficient. Stun seconds =
    /// <c>clamp(this × impactSpeed, WallStunSeconds, MaxStunSeconds)</c>.
    /// UP: hard impacts produce long freezes; soft brushes are still brief.
    /// DOWN: stun is always near the floor regardless of impact strength.
    /// </param>
    /// <param name="MaxStunSeconds">
    /// Absolute cap on stun duration, seconds. Prevents back-to-back hits from
    /// permanently locking the car.
    /// UP: pile-ups can freeze the car for longer windows.
    /// DOWN: even the worst crash recovers quickly.
    /// </param>
    /// <param name="TractionCircleGain">
    /// Traction-circle coupling strength between throttle, speed, and available steer
    /// authority.
    /// <c>demand = TractionCircleGain × max(0, throttle) × speedFrac</c>;
    /// <c>allowedFrac = max(MinSteerAuthority, sqrt(max(0, 1 − demand²)))</c>;
    /// effective max steer = <c>MaxSteer × allowedFrac</c>.
    /// UP: flooring throttle at speed crushes steer authority — must roll on throttle
    /// progressively to keep cornering grip.
    /// DOWN (0): no coupling; throttle and steer are independent.
    /// </param>
    /// <param name="MinSteerAuthority">
    /// Floor on steer authority when the traction circle saturates, 0..1. Effective
    /// max steer never drops below <c>MaxSteer × this</c>.
    /// UP: even at full throttle + top speed the car can still claw around corners;
    /// panicked inputs are forgiven.
    /// DOWN: traction-circle saturation nearly removes steering — punishes "floor
    /// it and hope" play.
    /// </param>
    /// <param name="HighSpeedSteerRateFactor">
    /// Speed-dependent steer-rate slowdown. Effective steer rate =
    /// <c>SteerRate × max(0.25, 1 − HighSpeedSteerRateFactor × speedFrac²)</c>.
    /// UP: high-speed steer changes become sluggish; must bleed speed before reacting
    /// to corners. Kills high-frequency wobble.
    /// DOWN (0): wheel response is the same at all speeds; allows flick-flick
    /// micro-corrections on straights.
    /// </param>
    /// <param name="SpeedLateralGripScale">
    /// Speed-scaled lateral-grip falloff. Effective on-track grip =
    /// <c>LateralGripFactor / (1 + SpeedLateralGripScale × speedFrac²)</c>. Encodes the
    /// v²/r centripetal cost. Kerb and off-track paths bypass this.
    /// UP: hard speed ceiling per corner radius — fast sweepers force lift, hairpins
    /// force heavy braking.
    /// DOWN (0): grip is speed-invariant; the car can take any corner at any speed.
    /// </param>
    /// <param name="TrailBrakeAuthorityWeight">
    /// Brake contribution to traction-circle demand (0..1). 1.0 = brake taxes the
    /// circle as much as throttle; 0.0 = braking is free.
    /// UP: trail-braking costs steer authority — encourages classical "brake straight,
    /// lift, turn-in" lines.
    /// DOWN (0): can brake and corner simultaneously with no grip penalty.
    /// </param>
    /// <param name="StraightLineAeroBoost">
    /// Straight-line aero (DRS-style) speed-cap boost. While the streak timer is positive,
    /// speed cap is multiplied by <c>1 + StraightLineAeroBoost × ramp</c>, with
    /// <c>ramp = clamp01(streak / StraightLineAeroRampSec)</c>.
    /// UP: top speed on long straights climbs further above <see cref="MaxSpeed"/>
    /// after a sustained straight.
    /// DOWN (0): no aero bonus — top speed is uniform regardless of straight length.
    /// </param>
    /// <param name="StraightLineAeroRampSec">
    /// Seconds of held-straight needed to reach full aero ramp.
    /// UP: aero bonus takes longer to build — rewards only the longest straights.
    /// DOWN: aero kicks in almost immediately after any steer release.
    /// </param>
    /// <param name="StraightLineAeroRecoverySec">
    /// Delay before the aero streak starts re-building after any qualifying steer input.
    /// The streak resets to <c>-StraightLineAeroRecoverySec</c>.
    /// UP: any micro-correction kills aero for longer — only laser-straight driving
    /// keeps the boost.
    /// DOWN: aero resumes ramping right after the wheel re-centres.
    /// </param>
    /// <param name="StraightLineAeroSteerThreshold">
    /// Steering-angle deadband for the aero streak, radians. Steer below this counts
    /// as "holding straight"; steer above resets the streak.
    /// UP: only laser-precise straight-line driving qualifies for aero.
    /// DOWN: small drifts and corrections still count as "straight"; aero stays active
    /// through wobble.
    /// </param>
    public readonly record struct CarParameters(
        float WheelBase,
        float MaxSteer,
        float MaxAccel,
        float MaxBrake,
        float MaxSpeed,
        float DragCoefficient,
        float SteerRate,
        float Gravity,
        float BoostThrust,
        float BoostDurationSec,
        float BoostRechargeRate,
        float OffTrackDragMul = 1f,
        float OffTrackSpeedCapFrac = 1f,
        float LateralGripFactor = 0f,
        float OffTrackGripFactor = 0f,
        float SpeedInducedUndersteerGain = 0f,
        float SlipReleaseFactor = 1f,
        float MinCruiseSpeed = 0f,
        float LowSpeedTurnBonus = 0f,
        float KerbGripFactor = 0f,
        float WallBounceDamping = 0.10f,
        float WallNormalRestitution = 0.15f,
        float CarCollisionRadius = 0.4f,
        float OffKerbCorneringPenalty = 0f,
        float OffKerbCorneringSteerThreshold = 0f,
        float WallStunSeconds = 0f,
        float WallDamageCoefficient = 0f,
        float WallDamageMinPerHit = 0f,
        float MinHealthSpeedFactor = 1f,
        float WallStunSecondsPerImpactSpeed = 0f,
        float MaxStunSeconds = 0f,
        float TractionCircleGain = 0f,
        float MinSteerAuthority = 1f,
        float HighSpeedSteerRateFactor = 0f,
        float SpeedLateralGripScale = 0f,
        float TrailBrakeAuthorityWeight = 0f,
        float StraightLineAeroBoost = 0f,
        float StraightLineAeroRampSec = 0f,
        float StraightLineAeroRecoverySec = 0f,
        float StraightLineAeroSteerThreshold = 0f);
}

