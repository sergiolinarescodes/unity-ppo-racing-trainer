using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.AiDriver.Physics.Modifiers;
using UnityPpoRacingTrainer.Core.AiDriver.Versions.Manifest;
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
        // Slipstream constants moved out of baked-const land into per-version
        // injected DraftingSettings (Phase 2). Field semantics match the
        // historical const names; doc comments live on the DraftingSettings
        // record. Sane defaults remain reachable through `new DraftingSettings()`
        // which mirrors the historical baked values (8 / 2.5 / 0.33 / 0.09625
        // / 6 / 3 / 0.05 / 0.7).
        private readonly float _draftMaxDistance;
        private readonly float _draftLateralTolerance;
        private readonly float _draftDragReduction;
        private readonly float _draftAccelBoost;
        private readonly float _draftMinActivationMS;
        private readonly float _launchGraceSec;
        private readonly float _attackTau;
        private readonly float _releaseTau;

        private readonly Dictionary<CarId, Sample> _samples = new();
        private readonly Dictionary<CarId, float> _smoothed = new();
        private readonly Dictionary<CarId, CarId?> _leader = new();
        private readonly Dictionary<CarId, float> _aliveTime = new();

        // Convenience overload for scenarios + tests that don't need a custom
        // tuning. Falls back to the historical baked defaults via DraftingSettings'
        // init values.
        public DraftService(IEventBus eventBus) : this(eventBus, new DraftingSettings()) { }

        public DraftService(IEventBus eventBus, DraftingSettings settings) : base(eventBus)
        {
            _draftMaxDistance = settings.MaxDistance;
            _draftLateralTolerance = settings.LateralTolerance;
            _draftDragReduction = settings.DragReduction;
            _draftAccelBoost = settings.AccelBoost;
            _draftMinActivationMS = settings.MinActivationMS;
            _launchGraceSec = settings.LaunchGraceSec;
            _attackTau = settings.AttackTau;
            _releaseTau = settings.ReleaseTau;
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
            if (speed <= _draftMinActivationMS) return parameters;
            // Leader-side gate — if the car ahead is also still launching,
            // no tow. Prevents grid-tight pack at race start from showering
            // the back rows with slingshot accel before anyone is up to
            // racing speed.
            if (_leader.TryGetValue(carId, out var leaderId) && leaderId.HasValue
                && _samples.TryGetValue(leaderId.Value, out var leaderSmp)
                && leaderSmp.Speed <= _draftMinActivationMS)
            {
                return parameters;
            }
            float gateRange = Mathf.Max(0.01f, parameters.MaxSpeed - _draftMinActivationMS);
            float gateT = Mathf.Clamp01((speed - _draftMinActivationMS) / gateRange);
            float speedGate = gateT * gateT;
            // Launch grace — fade tow in over the first _launchGraceSec of
            // the car's life so sector-1 chaos doesn't get amplified.
            float alive = _aliveTime.TryGetValue(carId, out var t) ? t : 0f;
            float launchGate = Mathf.Clamp01(alive / Mathf.Max(0.01f, _launchGraceSec));
            float gate = speedGate * launchGate;
            float dragMul = Mathf.Clamp01(1f - _draftDragReduction * s * gate);
            float accelMul = 1f + _draftAccelBoost * s * gate;
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
                if (forwardDist <= 0.5f || forwardDist > _draftMaxDistance) continue;

                // Lateral component magnitude.
                Vector2 right = new(myForward.y, -myForward.x);
                float lateral = Mathf.Abs(Vector2.Dot(rel, right));
                if (lateral > _draftLateralTolerance) continue;

                float proximity = 1f - forwardDist / _draftMaxDistance;
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
            float tau = rawDraft > current ? _attackTau : _releaseTau;
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
