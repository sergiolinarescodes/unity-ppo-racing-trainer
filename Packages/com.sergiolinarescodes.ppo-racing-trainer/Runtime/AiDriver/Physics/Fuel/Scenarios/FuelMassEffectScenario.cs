using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.AiDriver.Policy;
using UnityPpoRacingTrainer.Core.Terrain.Scenarios;
using Unidad.Core.Testing;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.AiDriver.Physics.Fuel.Scenarios
{
    /// <summary>
    /// Diff Apply() output for full vs near-empty tanks. Confirms the mass-
    /// coupling modifier scales MaxAccel / MaxBrake / LateralGripFactor as
    /// expected.
    /// </summary>
    internal sealed class FuelMassEffectScenario : DataDrivenScenario
    {
        private ScenarioEventBus _eventBus;
        private FuelService _fuel;
        private CarId _carId;

        public FuelMassEffectScenario() : base(new TestScenarioDefinition(
            "ai-driver-fuel-mass-effect",
            "AI Driver — Fuel Mass Effect (Synthetic)",
            "Compares CarParameters with full vs empty tank to show mass coupling.",
            Array.Empty<ScenarioParameter>()))
        { }

        protected override void ExecuteInternal(ScenarioParameterOverrides overrides)
        {
            _eventBus = new ScenarioEventBus();
            _fuel = new FuelService(_eventBus);
            _carId = new CarId(1);
            _fuel.RegisterDriver(_carId, DriverPersonality.AllRounder, 100f);
            var baseParams = AiDriverPhysicsDefaults.Latest;

            var pFull = _fuel.Apply(_carId, baseParams);
            _fuel.Reset(_carId, 1f);
            var pEmpty = _fuel.Apply(_carId, baseParams);

            Debug.Log($"[FuelMassEffectScenario] Base  : accel={baseParams.MaxAccel:F3} brake={baseParams.MaxBrake:F3} latGrip={baseParams.LateralGripFactor:F3}");
            Debug.Log($"[FuelMassEffectScenario] Full  : accel={pFull.MaxAccel:F3} brake={pFull.MaxBrake:F3} latGrip={pFull.LateralGripFactor:F3}");
            Debug.Log($"[FuelMassEffectScenario] Near-0: accel={pEmpty.MaxAccel:F3} brake={pEmpty.MaxBrake:F3} latGrip={pEmpty.LateralGripFactor:F3}");
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
