using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.AiDriver.Policy;
using UnityPpoRacingTrainer.Core.AiDriver.Training;
using Unidad.Core.Abstractions;
using Unidad.Core.EventBus;
using Unidad.Core.Systems;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.AiDriver.Physics.CarCollision
{
    public interface ICarCollisionService
    {
        void RegisterDriver(CarId carId, DriverPersonality personality);
    }

    /// <summary>
    /// Post-physics car-car collision resolver. Implements <see cref="IFixedTickable"/>
    /// so the framework drives it after <c>CarSimulationService</c>. Each pair of
    /// active cars is checked for circle-vs-circle overlap (radius =
    /// <c>CarParameters.CarCollisionRadius</c>); on hit we push them apart along
    /// the separation normal, apply equal-and-opposite 1D impulses, and deal
    /// quadratic damage proportional to the closing speed. Damage and stun
    /// scale by the cars' <see cref="DriverPersonality.PassingAggression"/> so
    /// "aggressive" drivers eat less self-damage from contact and the policy
    /// can learn that bumping is cheap for that archetype.
    /// </summary>
    internal sealed class CarCollisionService : SystemServiceBase, ICarCollisionService, IFixedTickable
    {
        /// <summary>
        /// Coefficient of restitution for car-car contact (0..1). 0 = perfectly
        /// inelastic (cars stick together along the contact normal); 1 = perfect
        /// elastic bounce.
        /// UP: cars bounce off each other dramatically — incidents have more kick.
        /// DOWN: cars absorb energy on contact and stay tangled — encourages
        /// avoidance.
        /// </summary>
        private const float Restitution = 0.25f;

        /// <summary>
        /// Floor restitution applied to same-direction (rear-end, side-swipe)
        /// contacts where the alignment lerp would otherwise collapse to 0.
        /// Small but non-zero — cars still nudge apart with a soft pop instead
        /// of going fully inelastic and gluing to each other.
        /// UP: same-direction contacts kick the cars off each other harder.
        /// DOWN (0): rear-ends become perfectly inelastic — sticking returns.
        /// </summary>
        private const float MinAlignedRestitution = 0.06f;

        /// <summary>
        /// Range multiplier on summed car radii used for the contact test.
        /// 1.0 = touch when bounding circles intersect; &lt; 1.0 = cars must
        /// overlap before a contact fires (later detection → fewer phantom
        /// taps but harder snap when they do hit).
        /// UP: contact triggers earlier — softer racing, more avoidance.
        /// DOWN: cars must really mesh before damage applies.
        /// </summary>
        private const float ContactRangeFactor = 0.8f;

        /// <summary>
        /// Damage coefficient on closing speed. Quadratic in the low-speed
        /// band, capped to linear above <see cref="DamageLinearAboveMS"/> so
        /// accidental high-speed clips disturb rather than obliterate. Final
        /// damage = <c>CarCarDamageCoefficient × min(s², linearCap × s) × lowSpeedScale</c>.
        /// UP: car-car contact is catastrophic — overtakes must be clean.
        /// DOWN: trading paint becomes viable; passing aggression pays off.
        /// </summary>
        private const float CarCarDamageCoefficient = 0.00675f;

        /// <summary>
        /// Closing-speed (m/s) above which damage stops growing quadratically
        /// and grows linearly only. Below this the quadratic shape keeps a
        /// usable signal for the policy; above this an accidental high-speed
        /// clip during an overtake disturbs without wrecking either car.
        /// Disturbance still scales fully via the 1D impulse and the new
        /// tangential friction term — physics, not damage, slows them down.
        /// </summary>
        private const float DamageLinearAboveMS = 6f;

        /// <summary>
        /// Tangential friction coefficient at car-car contact (0..1).
        /// Fraction of along-contact velocity dissipated as each car scrapes
        /// past the other. Models the natural slowdown of side-by-side
        /// contact without launching either car (the normal impulse is
        /// already restitution-limited).
        /// UP: scrapes shed lots of speed; pack-traffic naturally bunches up.
        /// DOWN (0): contact only resolves penetration; cars slide past with
        /// no speed loss.
        /// </summary>
        private const float TangentialFriction = 0.08f;

        /// <summary>
        /// Penetration-bias separation gain (per metre of overlap → m/s of
        /// outward velocity). Applied along the contact normal every tick
        /// the cars overlap, regardless of whether they are still closing.
        /// Stops cars from "sticking" together after the normal impulse and
        /// tangential friction equalize their velocities: even at zero
        /// relative velocity an overlap kicks them slightly apart in
        /// velocity-space, breaking contact in 1-2 ticks. No stun, no
        /// freeze — they just shrug and pull apart.
        /// UP: cars bounce off contact more decisively; close-pack racing
        /// gets jittery.
        /// DOWN (0): cars can sit glued together after equalising velocities;
        /// the geometric Separate() does the work alone and contact may
        /// re-fire each tick.
        /// </summary>
        private const float SeparationBiasGain = 8f;

        /// <summary>
        /// Speed coupling on the separation-bias kick. Final bias =
        /// <c>pen × SeparationBiasGain × (1 + SpeedBiasFactor × relMagnitude)</c>.
        /// A slow shrug (relMagnitude≈0) keeps the pure-penetration kick;
        /// a 10 m/s side-swipe scales the bias up ≈4× so the cars pop apart
        /// proportional to the violence of the encounter. Uses total
        /// relative-velocity magnitude so glancing high-speed clips
        /// (small normal closing, big tangential) still feel like real hits.
        /// UP: high-speed contacts shoot cars further apart on rebound.
        /// DOWN (0): bias kick ignores encounter speed; slow stalls and
        /// 12 m/s slams separate identically.
        /// </summary>
        private const float SpeedBiasFactor = 0.3f;

        /// <summary>
        /// Absolute cap on the separation-bias multiplier from the speed
        /// term, so head-on closing pairs at unusual speeds don't produce
        /// unbounded kicks. Multiplier clamps to this — at default 6 it
        /// caps the speed coupling at ~17 m/s relative encounter (above
        /// that, additional speed no longer grows the bias).
        /// </summary>
        private const float MaxBiasSpeedMul = 6f;

        /// <summary>
        /// Closing-speed (m/s) below which the quadratic damage gets scaled
        /// down toward <see cref="LowSpeedDamageFloor"/>. Lets cars squeeze
        /// past stationary / very-slow traffic without piling up HP loss —
        /// addresses the "agglomeration freeze" pattern where the pack stalls
        /// behind a wreck because every gentle nudge still costs them.
        /// </summary>
        private const float LowSpeedDamageThresholdMS = 3f;

        /// <summary>
        /// Floor multiplier on damage at ~0 m/s closing speed. Damage scales
        /// linearly from this floor at 0 m/s up to 1.0 at
        /// <see cref="LowSpeedDamageThresholdMS"/>.
        /// </summary>
        private const float LowSpeedDamageFloor = 0.1f;

        /// <summary>
        /// Heading perturbation per (m/s) of <em>total relative</em> speed
        /// between the cars at contact, in radians. Off-centre bumps rotate
        /// each chassis a few degrees (opposite signs) — "lose balance" —
        /// so velocity momentarily diverges from forward and the slip-grip
        /// term resolves the wobble naturally over the next few ticks. No
        /// input lockout, no freeze — pure physics reaction.
        /// At 12 m/s relative encounter: ±0.024 × 12 ≈ ±0.29 rad (~16°).
        /// UP: every bump becomes a visible chassis wobble — incidents read
        /// dramatically, recoveries demand counter-steer.
        /// DOWN: contact is silent in heading — only the impulse pushes them
        /// apart, no rotational signal.
        /// </summary>
        private const float HeadingPerturbPerRelMS = 0.024f;

        /// <summary>
        /// Absolute cap on heading perturbation per contact, radians (~25°).
        /// Prevents pathological closing speeds (head-ons in tight pack
        /// traffic) from spinning the car nearly 90° in a single tick.
        /// </summary>
        private const float MaxHeadingPerturb = 0.45f;

        /// <summary>
        /// Maximum random jitter applied per car per contact, radians (~2.3°).
        /// Layered on top of the deterministic perturbation so identical
        /// contacts read slightly different — kills the perfectly mirrored
        /// look of paired hits and gives bumps a hand-shaken feel. Jitter
        /// magnitude is sampled symmetrically in [-J, +J] per car, scales
        /// with closing speed (no jitter on near-zero taps), and respects
        /// the aggression dial.
        /// UP: hits look noisier — chassis snaps look organic but recoveries
        /// take longer.
        /// DOWN (0): paired wobbles are mirror-perfect — synthetic-looking.
        /// </summary>
        private const float HeadingJitterRad = 0.04f;

        private readonly ICarSimulationService _sim;
        private readonly IStageIdProvider _stage;
        private readonly Dictionary<CarId, DriverPersonality> _personality = new();
        private readonly List<CarId> _active = new();

        public CarCollisionService(IEventBus eventBus, ICarSimulationService sim, IStageIdProvider stage = null) : base(eventBus)
        {
            _sim = sim;
            _stage = stage;
        }

        public void RegisterDriver(CarId carId, DriverPersonality personality) => _personality[carId] = personality;

        public void FixedTick(float fixedDeltaTime)
        {
            // Stage-0 warmup: cars phase through each other. No pair check, no
            // separation, no impulse, no damage, no event. Resumes at stage ≥ 1.
            if (_stage != null && _stage.Resolver != null && _stage.Resolve() == 0) return;

            _active.Clear();
            foreach (var id in _sim.ActiveCars) _active.Add(id);

            for (int i = 0; i < _active.Count; i++)
            {
                if (!_sim.TryGetState(_active[i], out var sa)) continue;
                if (!_sim.TryGetParameters(_active[i], out var pa)) continue;
                for (int j = i + 1; j < _active.Count; j++)
                {
                    if (!_sim.TryGetState(_active[j], out var sb)) continue;
                    if (!_sim.TryGetParameters(_active[j], out var pb)) continue;

                    Vector2 d = new(sb.Position.x - sa.Position.x, sb.Position.z - sa.Position.z);
                    float dist = d.magnitude;
                    float minDist = (pa.CarCollisionRadius + pb.CarCollisionRadius) * ContactRangeFactor;
                    if (dist <= 1e-4f || dist >= minDist) continue;

                    Vector2 n = d / dist;
                    float pen = minDist - dist;

                    // Equal mass push-out: half each. Positional separation
                    // only — feeding metres of penetration into ApplyImpulse
                    // would inject them as m/s and compound every FixedTick.
                    Vector2 nudge = n * (pen * 0.5f);
                    _sim.Separate(_active[i], -nudge);
                    _sim.Separate(_active[j], nudge);

                    // Penetration-bias separation. Applied EVERY overlap
                    // tick — even when the cars are no longer approaching —
                    // so equalised velocities cannot leave them glued.
                    // Scaled by the total relative-speed magnitude so a
                    // high-speed clip pops the cars apart harder than a
                    // slow nudge. Splits half/half along the normal.
                    Vector2 vRelBias = sa.VelocityXZ - sb.VelocityXZ;
                    float relMagnitude = vRelBias.magnitude;
                    float speedMul = Mathf.Min(MaxBiasSpeedMul, 1f + SpeedBiasFactor * relMagnitude);
                    float biasMag = pen * SeparationBiasGain * speedMul;
                    if (biasMag > 0f)
                    {
                        Vector2 biasImpulse = n * (biasMag * 0.5f);
                        _sim.ApplyImpulse(_active[i], -biasImpulse);
                        _sim.ApplyImpulse(_active[j],  biasImpulse);
                    }

                    // 1D impulse on the separation axis.
                    float vaN = sa.VelocityXZ.x * n.x + sa.VelocityXZ.y * n.y;
                    float vbN = sb.VelocityXZ.x * n.x + sb.VelocityXZ.y * n.y;
                    float rel = vaN - vbN;            // closing speed along normal (positive => approaching)
                    // Not closing → skip the full collision response (impulse
                    // bounce, friction, damage, heading kick). Bias above
                    // already nudged them apart in velocity-space.
                    if (rel <= 0f) continue;

                    // Alignment-aware restitution. Cars moving in the same
                    // direction (rear-end, side-swipe) get r→0 — the
                    // contact is perfectly inelastic, closing velocity
                    // matches out, no visible bounce. Cars meeting head-on
                    // or perpendicular keep the full Restitution. Dot of
                    // velocity unit vectors: 1 = parallel, 0 = perpendicular,
                    // −1 = opposing.
                    float sa2 = sa.VelocityXZ.sqrMagnitude;
                    float sb2 = sb.VelocityXZ.sqrMagnitude;
                    float alignment = 0f;
                    if (sa2 > 0.01f && sb2 > 0.01f)
                    {
                        Vector2 dirA = sa.VelocityXZ / Mathf.Sqrt(sa2);
                        Vector2 dirB = sb.VelocityXZ / Mathf.Sqrt(sb2);
                        alignment = dirA.x * dirB.x + dirA.y * dirB.y;
                    }
                    float dynamicRestitution = Mathf.Lerp(Restitution, MinAlignedRestitution, Mathf.Clamp01(alignment));
                    float impulseMag = -(1f + dynamicRestitution) * rel * 0.5f;
                    Vector2 impulse = n * impulseMag;
                    _sim.ApplyImpulse(_active[i], impulse);
                    _sim.ApplyImpulse(_active[j], -impulse);

                    // Tangential friction. Cars rubbing past each other shed
                    // a fraction of their along-contact velocity — natural
                    // slowdown from contact without any launch behaviour
                    // (normal axis is restitution-bounded, tangent is pure
                    // damping). t is the right-hand tangent of the contact
                    // normal in XZ.
                    Vector2 tdir = new(-n.y, n.x);
                    float vaT = sa.VelocityXZ.x * tdir.x + sa.VelocityXZ.y * tdir.y;
                    float vbT = sb.VelocityXZ.x * tdir.x + sb.VelocityXZ.y * tdir.y;
                    _sim.ApplyImpulse(_active[i], tdir * (-TangentialFriction * vaT));
                    _sim.ApplyImpulse(_active[j], tdir * (-TangentialFriction * vbT));

                    float impactSpeed = Mathf.Abs(rel);
                    // Low-speed damage scaling: linear taper from
                    // LowSpeedDamageFloor at 0 m/s to 1.0 at the threshold.
                    // Stops slow-traffic incidents from compounding HP loss.
                    float lowSpeedScale = Mathf.Lerp(
                        LowSpeedDamageFloor, 1f,
                        Mathf.Clamp01(impactSpeed / LowSpeedDamageThresholdMS));
                    // Damage curve: quadratic up to DamageLinearAboveMS, then
                    // linear. An accidental clip at 12 m/s now costs the
                    // damage of an effective 12×6=72 (linear) instead of
                    // 144 (quadratic) — half. Disturbance from the impulse
                    // + tangential friction is unchanged.
                    float effectiveSq = impactSpeed <= DamageLinearAboveMS
                        ? impactSpeed * impactSpeed
                        : DamageLinearAboveMS * impactSpeed;
                    float baseDmg = CarCarDamageCoefficient * effectiveSq * lowSpeedScale;
                    _sim.ApplyDamage(_active[i], baseDmg * AggressionDamageScale(_active[i]));
                    _sim.ApplyDamage(_active[j], baseDmg * AggressionDamageScale(_active[j]));

                    // "Lose balance" heading perturbation. Uses the FULL
                    // relative-speed magnitude (not just along-normal closing)
                    // so a high-speed side-swipe with a small lateral closing
                    // component still produces a visible chassis rotation. The
                    // sign is taken from the cross-product of the contact
                    // normal and the tangential relative velocity, so each car
                    // rotates AWAY from where it got tagged — like a real
                    // glancing blow. Aggression dial trims the rotation for
                    // aggressive drivers (they "fight through" contact more).
                    float vRelCross = n.x * vRelBias.y - n.y * vRelBias.x; // signed lateral component
                    float yawSign = vRelCross >= 0f ? 1f : -1f;
                    float yawMag = Mathf.Min(MaxHeadingPerturb, relMagnitude * HeadingPerturbPerRelMS);
                    // Speed-scaled random jitter — kills mirror-perfect paired
                    // wobbles. Independent sample per car. Vanishes at low
                    // closing speed (slow stalls stay clean).
                    float jitterScale = Mathf.Clamp01(relMagnitude / 8f);
                    float jitterA = UnityEngine.Random.Range(-HeadingJitterRad, HeadingJitterRad) * jitterScale;
                    float jitterB = UnityEngine.Random.Range(-HeadingJitterRad, HeadingJitterRad) * jitterScale;
                    _sim.PerturbHeading(_active[i], (+yawSign * yawMag + jitterA) * AggressionImpactScale(_active[i]));
                    _sim.PerturbHeading(_active[j], (-yawSign * yawMag + jitterB) * AggressionImpactScale(_active[j]));

                    Vector3 contact = new(
                        (sa.Position.x + sb.Position.x) * 0.5f,
                        (sa.Position.y + sb.Position.y) * 0.5f,
                        (sa.Position.z + sb.Position.z) * 0.5f);
                    Publish(new CarHitCarEvent(_active[i], _active[j],
                        contact,
                        new Vector3(n.x, 0f, n.y),
                        impactSpeed));
                }
            }
        }

        // Damage taken from a contact scales inversely with the driver's
        // passing-aggression dial:
        //   scale = clamp(1 − 0.4 × PassingAggression, 0.4, 1.0)
        // UP (higher aggression): driver eats less self-damage from contact —
        // bumping becomes a cheap tool the policy can learn.
        // DOWN: contact costs the driver the full damage.
        private float AggressionDamageScale(CarId id)
        {
            float a = _personality.TryGetValue(id, out var p) ? p.PassingAggression : 0.5f;
            return Mathf.Clamp(1f - 0.4f * a, 0.4f, 1f);
        }

        // Heading-perturbation magnitude scales inversely with passing
        // aggression — aggressive drivers shrug off the contact wobble:
        //   scale = clamp(1 − 0.5 × PassingAggression, 0.4, 1.0)
        // No stun, no cooldown — used once at the moment of impact only.
        // UP: aggressive drivers barely flinch on contact.
        // DOWN: every car wobbles the full base amount.
        private float AggressionImpactScale(CarId id)
        {
            float a = _personality.TryGetValue(id, out var p) ? p.PassingAggression : 0.5f;
            return Mathf.Clamp(1f - 0.5f * a, 0.4f, 1f);
        }
    }
}
