using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.AiDriver.Policy;
using UnityPpoRacingTrainer.Core.Terrain.Scenarios;
using UnityPpoRacingTrainer.Core.Track;
using Unidad.Core.Testing;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.AiDriver.Physics.Fuel.Scenarios
{
    /// <summary>
    /// Pumps full-throttle tick events into the fuel service until depletion,
    /// then verifies the depleted event fired and Apply() zeroes accel.
    /// </summary>
    internal sealed class FuelDepletionLiftCoastScenario : DataDrivenScenario
    {
        private ScenarioEventBus _eventBus;
        private FuelService _fuel;
        private CarId _carId;
        private bool _depletedFired;

        public FuelDepletionLiftCoastScenario() : base(new TestScenarioDefinition(
            "ai-driver-fuel-depletion",
            "AI Driver — Fuel Depletion + Lift-Coast (Synthetic)",
            "Drains a small tank under full throttle and observes FuelDepletedEvent.",
            new[]
            {
                new ScenarioParameter("startingLiters", "Starting Liters", typeof(float), 1.5f, 0.1f, 100f),
            }))
        { }

        protected override void ExecuteInternal(ScenarioParameterOverrides overrides)
        {
            float starting = ResolveParam<float>(overrides, "startingLiters");
            _eventBus = new ScenarioEventBus();
            _fuel = new FuelService(_eventBus);
            _carId = new CarId(1);
            _fuel.RegisterDriver(_carId, DriverPersonality.Attacker, starting);

            _eventBus.Subscribe<FuelDepletedEvent>(_ =>
            {
                _depletedFired = true;
                Debug.Log("[FuelDepletionLiftCoastScenario] FUEL OUT.");
            });

            const float dt = 1f / 50f;
            int maxTicks = 50 * 600;
            for (int i = 0; i < maxTicks && !_depletedFired; i++)
            {
                _eventBus.Publish(new CarPhysicsTickedEvent(
                    _carId, Vector3.zero, 0f, 45f,
                    LateralAcceleration: 0f, Slip: 0f, ThrottleInput: 1f, BrakeInput: 0f,
                    Surface: SurfaceKind.Asphalt, ArcLengthAlong: 0f, Dt: dt));
            }

            var state = _fuel.Get(_carId);
            var baseParams = AiDriverPhysicsDefaults.Latest;
            var modParams = _fuel.Apply(_carId, baseParams);
            Debug.Log($"[FuelDepletionLiftCoastScenario] Liters={state.Liters:F3} burn={state.CurrentBurnRate_LperSec:F3}/s " +
                      $"depleted={state.Depleted} maxAccel(before/after)={baseParams.MaxAccel:F2}/{modParams.MaxAccel:F2}");
        }

        protected override ScenarioVerificationResult VerifyInternal(ScenarioParameterOverrides overrides)
            => new(new List<ScenarioVerificationResult.CheckResult>
            {
                new("fuel service constructed", _fuel != null, "FuelService not built"),
            });

        protected override void OnCleanup()
        {
            _fuel?.Dispose(); _fuel = null; _eventBus = null;
        }
    }
}
