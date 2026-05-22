using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.AiDriver.Physics.Modifiers;
using UnityPpoRacingTrainer.Core.AiDriver.Policy;
using Unidad.Core.EventBus;
using Unidad.Core.Systems;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.AiDriver.Physics.Fuel
{
    /// <summary>
    /// Frozen fuel state for a single car. <see cref="RollingLapsRemaining"/>
    /// is the circuit-agnostic observation: liters left / rolling-EMA of liters
    /// burned per lap. Clamped to [0, 5].
    /// </summary>
    public readonly record struct FuelState(
        float Liters,
        float CurrentBurnRate_LperSec,
        float RollingLapsRemaining,
        bool Depleted)
    {
        public static FuelState FullTank => new(100f, 0f, 5f, false);
    }

    public interface IFuelService
    {
        FuelState Get(CarId carId);
        void RegisterDriver(CarId carId, DriverPersonality personality, float startingLiters);
        void Reset(CarId carId, float liters);
        /// <summary>Force-clamp throttle to zero this tick if fuel is gone (called by injected wrapper).</summary>
        bool ForceCoast(CarId carId);
    }

    internal sealed class FuelService : SystemServiceBase, IFuelService, ICarPhysicsModifier
    {
        /// <summary>
        /// Idle burn rate, litres per second, charged even at zero throttle.
        /// UP: tank empties on its own when the car is stationary — penalises idling.
        /// DOWN: stationary cars never run dry; encourages stop-and-think behaviour.
        /// </summary>
        private const float IdleBurn_LperSec = 0.02f;

        /// <summary>
        /// Throttle-driven burn coefficient. Per-second burn from throttle =
        /// <c>ThrottleBurnCoeff × throttle × (1 + speedFrac) × aggressionMul</c>.
        /// UP: full-throttle laps drain the tank faster — strategic lift-and-coast
        /// becomes valuable.
        /// DOWN: throttle is nearly free; fuel ceases to be a constraint.
        /// </summary>
        private const float ThrottleBurnCoeff = 0.35f;

        /// <summary>
        /// Default tank capacity in litres when the caller does not supply one.
        /// UP: longer stints between empty — fewer fuel events per race.
        /// DOWN: shorter stints — fuel pressure dominates lap pacing.
        /// </summary>
        private const float DefaultTank_L = 100f;

        /// <summary>
        /// Max fraction by which a full tank reduces longitudinal performance
        /// (acceleration + braking). Effective scale = <c>1 / (1 + factor × fuelFrac)</c>.
        /// UP: heavy car effect is dramatic — empty tanks gain noticeably more pace.
        /// DOWN: fuel mass barely matters; full-tank pace ≈ empty-tank pace.
        /// </summary>
        private const float MaxFuelMassFactor = 0.15f;

        /// <summary>
        /// Mass-effect multiplier applied to the lateral-grip channel — lateral
        /// is less mass-sensitive than longitudinal, so the longitudinal factor
        /// is scaled by this on the way to grip.
        /// UP: fuel mass affects cornering grip almost as much as acceleration.
        /// DOWN (0): mass affects accel/brake but cornering grip is unchanged.
        /// </summary>
        private const float LateralMassFactor = 0.5f;

        private readonly Dictionary<CarId, Entry> _entries = new();

        public FuelService(IEventBus eventBus) : base(eventBus)
        {
            Subscribe<CarPhysicsTickedEvent>(OnTick);
            Subscribe<CarLapCompletedEvent>(OnLap);
        }

        public int Order => 200;

        public FuelState Get(CarId carId)
            => _entries.TryGetValue(carId, out var e)
                ? new FuelState(e.Liters, e.BurnRate, RollingLapsRemaining(e), e.Depleted)
                : FuelState.FullTank;

        public void RegisterDriver(CarId carId, DriverPersonality personality, float startingLiters)
        {
            _entries[carId] = new Entry
            {
                Personality = personality,
                Liters = Mathf.Max(0f, startingLiters),
                Capacity = Mathf.Max(1f, startingLiters > 0f ? startingLiters : DefaultTank_L),
                // Seed the rolling per-lap burn EMA so RollingLapsRemaining ≈ Liters at start.
                LapBurnEMA = 1f,
                LitersAtLapStart = startingLiters,
            };
        }

        public void Reset(CarId carId, float liters)
        {
            if (!_entries.TryGetValue(carId, out var e)) e = new Entry { Personality = DriverPersonality.Default };
            e.Liters = Mathf.Max(0f, liters);
            e.Depleted = false;
            e.LitersAtLapStart = liters;
            _entries[carId] = e;
        }

        public bool ForceCoast(CarId carId)
            => _entries.TryGetValue(carId, out var e) && e.Depleted;

        public CarParameters Apply(CarId carId, CarParameters parameters)
        {
            if (!_entries.TryGetValue(carId, out var e)) return parameters;
            if (e.Capacity <= 0f) return parameters;

            float fuelFrac = Mathf.Clamp01(e.Liters / e.Capacity);
            float longMass = 1f + MaxFuelMassFactor * fuelFrac;
            float latMass = 1f + MaxFuelMassFactor * LateralMassFactor * fuelFrac;
            float longScale = 1f / longMass;
            float latScale = 1f / latMass;

            // When the tank is empty, force a coast: zero throttle authority
            // and a reduced top-speed cap so the car only loses pace.
            // UP (closer to 1): less penalty for running dry.
            // DOWN (closer to 0): empty tank brings the car to a near-stop quickly.
            float depletedAccelScale = e.Depleted ? 0f : 1f;
            float depletedSpeedScale = e.Depleted ? 0.85f : 1f;

            return parameters with
            {
                MaxAccel = parameters.MaxAccel * longScale * depletedAccelScale,
                MaxBrake = parameters.MaxBrake * longScale,
                LateralGripFactor = parameters.LateralGripFactor * latScale,
                KerbGripFactor = parameters.KerbGripFactor * latScale,
                MaxSpeed = parameters.MaxSpeed * depletedSpeedScale,
            };
        }

        private void OnTick(CarPhysicsTickedEvent e)
        {
            if (!_entries.TryGetValue(e.Id, out var entry)) return;
            if (entry.Depleted) { entry.BurnRate = 0f; _entries[e.Id] = entry; return; }

            float aggressionMul = 1f + 0.4f * entry.Personality.RiskTolerance
                                       - 0.5f * entry.Personality.FuelEconomy;
            aggressionMul = Mathf.Clamp(aggressionMul, 0.5f, 1.6f);

            // 50 m/s is the reference top speed for shaping the burn curve.
            // UP: the speedFrac multiplier tops out later — faster cars don't
            // burn proportionally more fuel.
            // DOWN: small speed differences cause big burn-rate differences.
            float speedFrac = Mathf.Clamp01(e.Speed / 50f);
            float perSec = IdleBurn_LperSec
                + ThrottleBurnCoeff * e.ThrottleInput * (1f + speedFrac) * aggressionMul;

            entry.Liters = Mathf.Max(0f, entry.Liters - perSec * e.Dt);
            entry.BurnRate = perSec;

            if (entry.Liters <= 0f && !entry.Depleted)
            {
                entry.Depleted = true;
                Publish(new FuelDepletedEvent(e.Id));
            }

            _entries[e.Id] = entry;
        }

        private void OnLap(CarLapCompletedEvent e)
        {
            if (!_entries.TryGetValue(e.Id, out var entry)) return;
            float litersBurnedThisLap = Mathf.Max(0.0001f, entry.LitersAtLapStart - entry.Liters);
            // EMA smoothing of per-lap burn (α = 0.3).
            // UP (α → 1): the rolling estimate reacts instantly — noisy laps cause
            // wild RollingLapsRemaining swings.
            // DOWN (α → 0): the estimate is stable but slow to acknowledge a
            // sustained change in driving style.
            entry.LapBurnEMA = entry.LapBurnEMA <= 0f
                ? litersBurnedThisLap
                : Mathf.Lerp(entry.LapBurnEMA, litersBurnedThisLap, 0.3f);
            entry.LitersAtLapStart = entry.Liters;
            _entries[e.Id] = entry;
        }

        private static float RollingLapsRemaining(Entry e)
            => e.LapBurnEMA > 0f
                ? Mathf.Clamp(e.Liters / e.LapBurnEMA, 0f, 5f)
                : 5f;

        private struct Entry
        {
            public DriverPersonality Personality;
            public float Liters;
            public float Capacity;
            public float BurnRate;
            public float LapBurnEMA;
            public float LitersAtLapStart;
            public bool Depleted;
        }
    }
}
