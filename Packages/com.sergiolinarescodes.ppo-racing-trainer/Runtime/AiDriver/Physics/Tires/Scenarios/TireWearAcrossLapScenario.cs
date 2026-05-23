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
    /// Synthetic wear accumulation showcase. Pushes a scripted sequence of
    /// <see cref="CarPhysicsTickedEvent"/>s through the tire service (left turn,
    /// straight, right turn, brake zone) and logs L/R wear deltas to the console.
    /// No track / no integrator — pure unit-style scenario you can re-run from
    /// the Scenario Browser to eyeball wear curves under different personalities.
    /// </summary>
    internal sealed class TireWearAcrossLapScenario : DataDrivenScenario
    {
        private ScenarioEventBus _eventBus;
        private TirePhysicsService _tire;
        private CarId _carId;

        public TireWearAcrossLapScenario() : base(new TestScenarioDefinition(
            "ai-driver-tire-wear-across-lap",
            "AI Driver — Tire Wear Across Lap (Synthetic)",
            "Pushes scripted lateral-G / slip / brake events through the tire service " +
            "and logs L/R wear. Switch personality preset to see preserver vs risk-taker.",
            new[]
            {
                new ScenarioParameter("seconds", "Sim Seconds", typeof(int), 60, 5, 600),
                new ScenarioParameter("riskTolerance", "Risk Tolerance", typeof(float), 0.5f, 0f, 1f),
                new ScenarioParameter("tirePreservation", "Tire Preservation", typeof(float), 0.5f, 0f, 1f),
            }))
        { }

        protected override void ExecuteInternal(ScenarioParameterOverrides overrides)
        {
            int seconds = ResolveParam<int>(overrides, "seconds");
            float risk = ResolveParam<float>(overrides, "riskTolerance");
            float pres = ResolveParam<float>(overrides, "tirePreservation");

            _eventBus = new ScenarioEventBus();
            _tire = new TirePhysicsService(_eventBus, new StaticTrainingSettingsService());
            _carId = new CarId(1);

            var personality = DriverPersonality.Default with
            {
                RiskTolerance = risk,
                TirePreservation = pres,
            };
            _tire.RegisterDriver(_carId, personality);

            const float dt = 1f / 50f;
            int ticks = seconds * 50;
            // Repeating "lap" pattern: left curve, straight, right curve, brake zone.
            for (int i = 0; i < ticks; i++)
            {
                float phase = (i * dt) % 8f;
                float ay; float slip; float throttle; float brake; SurfaceKind surface = SurfaceKind.Asphalt;
                if (phase < 2f) { ay = 6f; slip = 0.10f; throttle = 0.6f; brake = 0f; }       // left curve (right tires loaded)
                else if (phase < 4f) { ay = 0f; slip = 0.0f; throttle = 1f; brake = 0f; }      // straight
                else if (phase < 6f) { ay = -6f; slip = 0.10f; throttle = 0.6f; brake = 0f; }   // right curve (left tires loaded)
                else { ay = 0f; slip = 0.05f; throttle = 0f; brake = 1f; surface = SurfaceKind.Kerb; } // brake into kerb

                _eventBus.Publish(new CarPhysicsTickedEvent(
                    _carId,
                    Vector3.zero,
                    0f,
                    Speed: 30f,
                    LateralAcceleration: ay,
                    Slip: slip,
                    ThrottleInput: throttle,
                    BrakeInput: brake,
                    Surface: surface,
                    ArcLengthAlong: (i * dt) * 30f,
                    Dt: dt));
            }

            var state = _tire.Get(_carId);
            Debug.Log($"[TireWearAcrossLapScenario] After {seconds}s: L={state.LeftWear:F3} R={state.RightWear:F3} " +
                      $"punctured(L,R)=({state.LeftPunctured},{state.RightPunctured})");
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
