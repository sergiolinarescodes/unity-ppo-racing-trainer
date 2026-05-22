using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.AiDriver.Config;
using UnityPpoRacingTrainer.Core.AiDriver.Physics.Modifiers;
using UnityPpoRacingTrainer.Core.AiDriver.Policy;
using UnityPpoRacingTrainer.Core.Track;
using Unidad.Core.EventBus;
using Unidad.Core.Systems;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.AiDriver.Physics.Tires
{
    /// <summary>
    /// Frozen tire condition for a single car. Left/Right wear is tracked
    /// independently so right-hand corners (which load the left tires) wear
    /// asymmetrically vs left-hand corners.
    /// </summary>
    public readonly record struct TireState(
        float LeftWear,
        float RightWear,
        bool LeftPunctured,
        bool RightPunctured)
    {
        public static TireState Fresh => new(0f, 0f, false, false);
        public float Worst => Mathf.Max(LeftWear, RightWear);
        public float Mean => 0.5f * (LeftWear + RightWear);
    }

    public interface ITirePhysicsService
    {
        TireState Get(CarId carId);
        void Reset(CarId carId);
        void RegisterDriver(CarId carId, DriverPersonality personality);
        /// <summary>Per-arc-length stress coefficient ≥ 1 from circuit profile (default 1).</summary>
        void SetCircuitStressProvider(Func<CarId, float, float> provider);
    }

    /// <summary>
    /// Subscribes to <see cref="CarPhysicsTickedEvent"/>, accumulates per-wheel
    /// wear, fires <see cref="TirePuncturedEvent"/> when worn tires meet high
    /// lateral G, and applies a grip multiplier via the modifier aggregator.
    /// All numeric coefficients are loaded from <see cref="ITrainingSettingsService"/>
    /// — edit <c>settings.json</c> at the repo root to tune without recompiling.
    /// </summary>
    internal sealed class TirePhysicsService : SystemServiceBase, ITirePhysicsService, ICarPhysicsModifier
    {
        // Wear coefficients (loaded from settings.json -> tirePhysics.*).
        private readonly float _kLateralG;
        private readonly float _kSlip;
        private readonly float _kBurnout;
        private readonly float _kBrake;
        private readonly float _hardBrakeThreshold;
        private readonly float _hardBrakePeakMul;
        private readonly float _hardBrakeExponent;
        private readonly float _brakeBlockadeInputThreshold;
        private readonly float _brakeBlockadeHoldSeconds;
        private readonly float _brakeBlockadePenaltyMul;
        private readonly float _kKerbStress;
        private readonly float _punctureThreshold;
        private readonly float _punctureGThreshold;
        private readonly float _punctureBaseChancePerSec;
        private readonly float _puncturedGripFactor;

        private readonly Dictionary<CarId, Entry> _entries = new();
        private Func<CarId, float, float> _circuitStress;

        public TirePhysicsService(IEventBus eventBus, ITrainingSettingsService settings) : base(eventBus)
        {
            var t = settings.Current.TirePhysics;
            _kLateralG = t.KLateralG;
            _kSlip = t.KSlip;
            _kBurnout = t.KBurnout;
            _kBrake = t.KBrake;
            _hardBrakeThreshold = t.HardBrakeThreshold;
            _hardBrakePeakMul = t.HardBrakePeakMul;
            _hardBrakeExponent = t.HardBrakeExponent;
            _brakeBlockadeInputThreshold = t.BrakeBlockadeInputThreshold;
            _brakeBlockadeHoldSeconds = t.BrakeBlockadeHoldSeconds;
            _brakeBlockadePenaltyMul = t.BrakeBlockadePenaltyMul;
            _kKerbStress = t.KKerbStress;
            _punctureThreshold = t.PunctureThreshold;
            _punctureGThreshold = t.PunctureGThreshold;
            _punctureBaseChancePerSec = t.PunctureBaseChancePerSec;
            _puncturedGripFactor = t.PuncturedGripFactor;

            Subscribe<CarPhysicsTickedEvent>(OnTick);
        }

        public int Order => 100;

        public TireState Get(CarId carId)
            => _entries.TryGetValue(carId, out var e)
                ? new TireState(e.LeftWear, e.RightWear, e.LeftPunctured, e.RightPunctured)
                : TireState.Fresh;

        public void Reset(CarId carId)
        {
            if (!_entries.TryGetValue(carId, out var e)) e = new Entry { Personality = DriverPersonality.Default };
            e.LeftWear = 0f;
            e.RightWear = 0f;
            e.LeftPunctured = false;
            e.RightPunctured = false;
            _entries[carId] = e;
        }

        public void RegisterDriver(CarId carId, DriverPersonality personality)
        {
            if (!_entries.TryGetValue(carId, out var e)) e = new Entry();
            e.Personality = personality;
            _entries[carId] = e;
        }

        public void SetCircuitStressProvider(Func<CarId, float, float> provider) => _circuitStress = provider;

        public CarParameters Apply(CarId carId, CarParameters parameters)
        {
            if (!_entries.TryGetValue(carId, out var e)) return parameters;

            float lScale = WearGripScale(e.LeftWear, e.LeftPunctured);
            float rScale = WearGripScale(e.RightWear, e.RightPunctured);
            // Effective lateral grip is bottlenecked by whichever side is more worn.
            float gripMul = Mathf.Min(lScale, rScale);
            float worstWear = Mathf.Max(e.LeftWear, e.RightWear);
            float longMul = Mathf.Lerp(1f, 0.9856f, Mathf.Clamp01(worstWear));
            return parameters with
            {
                LateralGripFactor = parameters.LateralGripFactor * gripMul,
                KerbGripFactor = parameters.KerbGripFactor * gripMul,
                MaxAccel = parameters.MaxAccel * longMul,
                MaxBrake = parameters.MaxBrake * longMul
            };
        }

        private float WearGripScale(float wear, bool punctured)
        {
            if (punctured) return _puncturedGripFactor;
            // Two-segment "F1 cliff" curve:
            //   [0.00, 0.90] — working zone: 1.00 → 0.856 (linear, 14.4% loss).
            //   [0.90, 1.00] — cliff: 0.856 → 0.388 (linear, 46.8% loss).
            if (wear < 0.9f) return 1f - 0.144f * (wear / 0.9f);
            return Mathf.Lerp(0.856f, 0.388f, Mathf.InverseLerp(0.9f, 1f, wear));
        }

        private void OnTick(CarPhysicsTickedEvent e)
        {
            if (!_entries.TryGetValue(e.Id, out var entry))
            {
                entry = new Entry { Personality = DriverPersonality.Default };
            }

            float aggressionMul = Mathf.Clamp(
                1f + 1.2f * entry.Personality.RiskTolerance - 0.6f * entry.Personality.TirePreservation,
                0.4f, 1.5f);
            float circuitMul = _circuitStress != null ? Mathf.Max(0.1f, _circuitStress(e.Id, e.ArcLengthAlong)) : 1f;
            float surfaceMul = e.Surface == SurfaceKind.Kerb ? _kKerbStress : 1f;

            float ay = Mathf.Abs(e.LateralAcceleration);
            float throttleOn = Mathf.Max(0f, e.ThrottleInput);
            float hardBrakeMul = 1f + (_hardBrakePeakMul - 1f) * Mathf.Pow(Mathf.Clamp01(e.BrakeInput), _hardBrakeExponent);
            if (e.BrakeInput >= _brakeBlockadeInputThreshold)
                entry.BrakeBlockadeHold += e.Dt;
            else
                entry.BrakeBlockadeHold = 0f;
            float blockadeMul = entry.BrakeBlockadeHold > _brakeBlockadeHoldSeconds ? _brakeBlockadePenaltyMul : 1f;
            float effBrakeMul = hardBrakeMul * blockadeMul;
            float perSecond = (
                _kLateralG * ay * ay +
                _kSlip * e.Slip * e.Slip +
                _kBurnout * e.Slip * throttleOn +
                _kBrake * e.BrakeInput * effBrakeMul)
                * aggressionMul * circuitMul * surfaceMul;

            float dWear = perSecond * e.Dt;
            if (e.LateralAcceleration > 0f) entry.LeftWear += dWear;
            else if (e.LateralAcceleration < 0f) entry.RightWear += dWear;
            else { entry.LeftWear += 0.5f * dWear; entry.RightWear += 0.5f * dWear; }

            float brakeWear = _kBrake * e.BrakeInput * effBrakeMul * aggressionMul * circuitMul * e.Dt;
            entry.LeftWear += brakeWear;
            entry.RightWear += brakeWear;

            entry.LeftWear = Mathf.Clamp01(entry.LeftWear);
            entry.RightWear = Mathf.Clamp01(entry.RightWear);

            if (!entry.LeftPunctured && entry.LeftWear > _punctureThreshold
                && e.LateralAcceleration > _punctureGThreshold
                && entry.Personality.RiskTolerance > 0.5f
                && UnityEngine.Random.value < _punctureBaseChancePerSec * (entry.LeftWear - _punctureThreshold) * 100f * e.Dt)
            {
                entry.LeftPunctured = true;
                Publish(new TirePuncturedEvent(e.Id, TireSide.Left, entry.LeftWear));
            }
            if (!entry.RightPunctured && entry.RightWear > _punctureThreshold
                && e.LateralAcceleration < -_punctureGThreshold
                && entry.Personality.RiskTolerance > 0.5f
                && UnityEngine.Random.value < _punctureBaseChancePerSec * (entry.RightWear - _punctureThreshold) * 100f * e.Dt)
            {
                entry.RightPunctured = true;
                Publish(new TirePuncturedEvent(e.Id, TireSide.Right, entry.RightWear));
            }

            _entries[e.Id] = entry;
        }

        private struct Entry
        {
            public DriverPersonality Personality;
            public float LeftWear;
            public float RightWear;
            public bool LeftPunctured;
            public bool RightPunctured;
            /// <summary>Seconds of continuous brake-pinned-to-max input. Resets when brake drops below threshold.</summary>
            public float BrakeBlockadeHold;
        }
    }
}
