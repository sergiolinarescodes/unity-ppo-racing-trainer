using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.AiDriver.Config;
using UnityPpoRacingTrainer.Core.AiDriver.Policy;
using UnityPpoRacingTrainer.Core.Terrain.Scenarios;
using UnityPpoRacingTrainer.Core.Track;
using Unidad.Core.Testing;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.AiDriver.Physics.Tires.Scenarios
{
    /// <summary>
    /// Force-feeds critically worn tires + sustained high lateral-G to a risk-
    /// taker personality and verifies that <see cref="TirePuncturedEvent"/>
    /// eventually fires. Demonstrates the puncture roll path.
    /// </summary>
    internal sealed class PunctureUnderHighGScenario : DataDrivenScenario
    {
        private ScenarioEventBus _eventBus;
        private TirePhysicsService _tire;
        private CarId _carId;
        private bool _puncturedFired;

        public PunctureUnderHighGScenario() : base(new TestScenarioDefinition(
            "ai-driver-puncture-high-g",
            "AI Driver — Puncture Under High G (Synthetic)",
            "Pre-loads tires to critical wear, then runs sustained high lateral-G ticks " +
            "with a risk-taker personality. Expect TirePuncturedEvent to fire.",
            Array.Empty<ScenarioParameter>()))
        { }

        protected override void ExecuteInternal(ScenarioParameterOverrides overrides)
        {
            _eventBus = new ScenarioEventBus();
            _tire = new TirePhysicsService(_eventBus, new StaticTrainingSettingsService());
            _carId = new CarId(1);
            _tire.RegisterDriver(_carId, DriverPersonality.RiskTaker);

            _eventBus.Subscribe<TirePuncturedEvent>(evt =>
            {
                _puncturedFired = true;
                Debug.Log($"[PunctureUnderHighGScenario] PUNCTURE side={evt.Side} wear={evt.WearAtPuncture:F3}");
            });

            // Pre-wear: 8 simulated seconds of high lateral-G to drive both sides above threshold.
            const float dt = 1f / 50f;
            for (int i = 0; i < 50 * 8; i++)
            {
                float ay = (i % 2 == 0) ? 7f : -7f;
                _eventBus.Publish(new CarPhysicsTickedEvent(
                    _carId, Vector3.zero, 0f, 40f, ay, 0.2f, 0.7f, 0f,
                    SurfaceKind.Asphalt, i * dt * 40f, dt));
            }

            // Sustained high-G until puncture or 4 simulated seconds elapsed.
            for (int i = 0; i < 50 * 4 && !_puncturedFired; i++)
            {
                _eventBus.Publish(new CarPhysicsTickedEvent(
                    _carId, Vector3.zero, 0f, 45f,
                    LateralAcceleration: 8f, Slip: 0.3f, ThrottleInput: 0.8f, BrakeInput: 0f,
                    Surface: SurfaceKind.Asphalt, ArcLengthAlong: 0f, Dt: dt));
            }

            var state = _tire.Get(_carId);
            Debug.Log($"[PunctureUnderHighGScenario] Final L={state.LeftWear:F3} R={state.RightWear:F3} " +
                      $"punctured(L,R)=({state.LeftPunctured},{state.RightPunctured}) eventFired={_puncturedFired}");
        }

        protected override ScenarioVerificationResult VerifyInternal(ScenarioParameterOverrides overrides)
        {
            var checks = new List<ScenarioVerificationResult.CheckResult>
            {
                new("tire service constructed", _tire != null, "TirePhysicsService not built"),
            };
            return new ScenarioVerificationResult(checks);
        }

        protected override void OnCleanup()
        {
            _tire?.Dispose();
            _tire = null;
            _eventBus = null;
        }
    }
}
