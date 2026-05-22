using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.AiDriver.Physics.Modifiers;
using Unidad.Core.EventBus;
using Unidad.Core.Systems;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.AiDriver.Physics.Draft
{
    public readonly record struct DraftState(float Strength, CarId? LeaderId);

    public interface IDraftService
    {
        DraftState Get(CarId carId);
    }

    /// <summary>
    /// Per-tick draft strength estimator. For each updated car, finds the
    /// nearest car ahead within a narrow cone, computes raw draft strength
    /// from proximity × cone, then smooths with asymmetric attack/release time
    /// constants so the slipstream "carries" partially with the car as it
    /// pulls out of the wake — the slingshot pass mechanic. Applies the
    /// smoothed strength as a drag-coefficient reduction via the modifier
    /// aggregator.
    /// </summary>
    internal sealed class DraftService : SystemServiceBase, IDraftService, ICarPhysicsModifier
    {
        /// <summary>
        /// Maximum forward-axis distance (metres) at which a leader's wake is
        /// felt. Cars farther ahead contribute zero raw draft.
        /// UP: draft reaches farther behind — easier to catch a tow on long
        /// straights even from a long way back.
        /// DOWN: draft is a knife-edge "tuck or no tuck" effect.
        /// </summary>
        private const float DraftMaxDistance = 8f;

        /// <summary>
        /// Lateral offset (metres) outside of which a leader contributes no
        /// draft, regardless of distance.
        /// UP: cars can pull side-by-side and still benefit — fewer hard
        /// commitment moments for overtakes.
        /// DOWN: draft requires near-perfect alignment; only true straight-line
        /// follows tow.
        /// </summary>
        private const float DraftLateralTolerance = 2.5f;

        /// <summary>
        /// Maximum drag-coefficient cut achievable at perfect tuck (0..1).
        /// Effective drag = <c>DragCoefficient × (1 − DraftDragReduction × smoothedStrength)</c>.
        /// 0.33 is intentionally arcadey: at the canonical pace the tow
        /// needs to feel decisive enough to drive overtake commitment, so
        /// catching the leader on a straight is a real reward.
        /// UP: tow is decisive — overtaking on long straights becomes routine.
        /// DOWN: tow is a marginal helper at best.
        /// </summary>
        private const float DraftDragReduction = 0.33f;

        /// <summary>
        /// Catch-up acceleration boost while behind a leader. Effective accel
        /// = <c>MaxAccel × (1 + DraftAccelBoost × smoothedStrength)</c>.
        /// At s≈0.2 (mild draft) → +~2%; at s=1.0 (perfect tuck) → +~10%.
        /// The tow meaningfully restores closing pace and the slingshot
        /// survives the brief commit-to-pass moment thanks to the
        /// asymmetric smoothing (ReleaseTau=0.7s).
        /// UP: catching up is decisive — gaps close fast on straights.
        /// DOWN: trailing cars match leader pace at best.
        /// </summary>
        private const float DraftAccelBoost = 0.09625f;

        /// <summary>
        /// Minimum trailing-car speed (m/s) before drag cut / catch-up accel
        /// turn on at all. Below this both are zero — race starts and slow
        /// off-line crawls get no draft assist. Above the threshold the gate
        /// ramps quadratically from 0 to 1 as speed climbs to MaxSpeed.
        /// At canonical MaxSpeed ≈ 10 m/s: 6 m/s = gate 0; 7 m/s ≈ 0.06;
        /// 8 m/s = 0.25; 10 m/s = 1.0. Leader-side gate is symmetric — if
        /// leader hasn't reached this either, no tow at all (kills the
        /// grid-launch pack slingshot). Race-start chaos is handled by
        /// <see cref="LaunchGraceSec"/>, not this threshold.
        /// </summary>
        private const float DraftMinActivationMS = 6f;

        /// <summary>
        /// Seconds after a car first appears during which draft strength is
        /// faded in from 0 → 1. Kills the launch-phase slingshot where the
        /// whole pack sits in each other's cone and back-row cars leapfrog
        /// the pole car through sector 1. UP: longer pure-launch window
        /// before tow engages — pole holds the lead deeper into lap 1.
        /// DOWN: tow returns sooner; chaotic restarts.
        /// </summary>
        private const float LaunchGraceSec = 3f;

        /// <summary>
        /// Time constant (seconds) for draft strength rising. The smoothed
        /// strength reaches ~63% of the raw value in <c>AttackTau</c> seconds.
        /// UP: tow takes longer to fill in — entering the wake is a slow build.
        /// DOWN: tow snaps to full strength instantly.
        /// </summary>
        private const float AttackTau = 0.05f;

        /// <summary>
        /// Time constant (seconds) for draft strength falling. The slingshot
        /// effect — slipstream "carries" out of the wake.
        /// UP: tow lingers long after pulling out — bigger slingshot.
        /// DOWN: pull-out kills tow immediately; no overtake bonus.
        /// </summary>
        private const float ReleaseTau = 0.7f;

        private readonly Dictionary<CarId, Sample> _samples = new();
        private readonly Dictionary<CarId, float> _smoothed = new();
        private readonly Dictionary<CarId, CarId?> _leader = new();
        private readonly Dictionary<CarId, float> _aliveTime = new();

        public DraftService(IEventBus eventBus) : base(eventBus)
        {
            Subscribe<CarPhysicsTickedEvent>(OnTick);
            Subscribe<CarSpawnedEvent>(e =>
            {
                // Reset alive-time so each (re)spawn re-arms the launch grace —
                // critical for training episode resets where the same CarId
                // is reused across episodes.
                _aliveTime[e.Id] = 0f;
                _smoothed[e.Id] = 0f;
                _leader[e.Id] = null;
            });
            Subscribe<CarDespawnedEvent>(e =>
            {
                _samples.Remove(e.Id);
                _smoothed.Remove(e.Id);
                _leader.Remove(e.Id);
                _aliveTime.Remove(e.Id);
            });
        }

        public int Order => 300;

        public DraftState Get(CarId carId)
        {
            float s = _smoothed.TryGetValue(carId, out var v) ? v : 0f;
            CarId? leader = _leader.TryGetValue(carId, out var l) ? l : null;
            return new DraftState(s, leader);
        }

        public CarParameters Apply(CarId carId, CarParameters parameters)
        {
            if (!_smoothed.TryGetValue(carId, out var s) || s <= 0f) return parameters;
            // Speed gate. Drag + catch-up accel are inert below
            // DraftMinActivationMS (race starts, slow crawls), then ramp
            // quadratically up to full effect at MaxSpeed.
            float speed = _samples.TryGetValue(carId, out var smp) ? smp.Speed : 0f;
            if (speed <= DraftMinActivationMS) return parameters;
            // Leader-side gate — if the car ahead is also still launching,
            // no tow. Prevents grid-tight pack at race start from showering
            // the back rows with slingshot accel before anyone is up to
            // racing speed.
            if (_leader.TryGetValue(carId, out var leaderId) && leaderId.HasValue
                && _samples.TryGetValue(leaderId.Value, out var leaderSmp)
                && leaderSmp.Speed <= DraftMinActivationMS)
            {
                return parameters;
            }
            float gateRange = Mathf.Max(0.01f, parameters.MaxSpeed - DraftMinActivationMS);
            float gateT = Mathf.Clamp01((speed - DraftMinActivationMS) / gateRange);
            float speedGate = gateT * gateT;
            // Launch grace — fade tow in over the first LaunchGraceSec of
            // the car's life so sector-1 chaos doesn't get amplified.
            float alive = _aliveTime.TryGetValue(carId, out var t) ? t : 0f;
            float launchGate = Mathf.Clamp01(alive / Mathf.Max(0.01f, LaunchGraceSec));
            float gate = speedGate * launchGate;
            float dragMul = Mathf.Clamp01(1f - DraftDragReduction * s * gate);
            float accelMul = 1f + DraftAccelBoost * s * gate;
            return parameters with
            {
                DragCoefficient = parameters.DragCoefficient * dragMul,
                MaxAccel = parameters.MaxAccel * accelMul,
            };
        }

        private void OnTick(CarPhysicsTickedEvent e)
        {
            _samples[e.Id] = new Sample(e.Position, e.Heading, e.Speed);
            _aliveTime[e.Id] = (_aliveTime.TryGetValue(e.Id, out var prevAlive) ? prevAlive : 0f) + e.Dt;

            float rawDraft = 0f;
            CarId? leaderId = null;

            // Forward direction from heading (XZ plane).
            Vector2 myForward = new(Mathf.Sin(e.Heading), Mathf.Cos(e.Heading));

            foreach (var kvp in _samples)
            {
                if (kvp.Key.Value == e.Id.Value) continue;
                var other = kvp.Value;
                Vector2 rel = new(other.Position.x - e.Position.x, other.Position.z - e.Position.z);
                float forwardDist = Vector2.Dot(rel, myForward);
                if (forwardDist <= 0.5f || forwardDist > DraftMaxDistance) continue;

                // Lateral component magnitude.
                Vector2 right = new(myForward.y, -myForward.x);
                float lateral = Mathf.Abs(Vector2.Dot(rel, right));
                if (lateral > DraftLateralTolerance) continue;

                float proximity = 1f - forwardDist / DraftMaxDistance;
                Vector2 otherForward = new(Mathf.Sin(other.Heading), Mathf.Cos(other.Heading));
                float cone = Mathf.Max(0f, Vector2.Dot(myForward, otherForward));
                float strength = proximity * cone;
                if (strength > rawDraft)
                {
                    rawDraft = strength;
                    leaderId = kvp.Key;
                }
            }

            // Asymmetric smoothing.
            float current = _smoothed.TryGetValue(e.Id, out var prev) ? prev : 0f;
            float tau = rawDraft > current ? AttackTau : ReleaseTau;
            float alpha = 1f - Mathf.Exp(-e.Dt / Mathf.Max(0.001f, tau));
            float smoothed = current + (rawDraft - current) * alpha;
            _smoothed[e.Id] = smoothed;

            bool leaderChanged = !_leader.TryGetValue(e.Id, out var prevLeader) || prevLeader?.Value != leaderId?.Value;
            if (leaderChanged || Mathf.Abs(smoothed - current) > 0.02f)
            {
                _leader[e.Id] = leaderId;
                Publish(new DraftStateChangedEvent(e.Id, leaderId, smoothed));
            }
        }

        private readonly record struct Sample(Vector3 Position, float Heading, float Speed);
    }
}
