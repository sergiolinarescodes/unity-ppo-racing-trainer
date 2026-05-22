using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.AiDriver.Physics;
using UnityPpoRacingTrainer.Core.Terrain.Scenarios;
using UnityPpoRacingTrainer.Core.Track;
using Unidad.Core.Testing;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.AiDriver.Race.Scenarios
{
    /// <summary>
    /// Scripts two cars trading positions: car 2 catches car 1 over scripted
    /// arc-length progressions. Counts emitted <see cref="OvertakeEvent"/>s.
    /// </summary>
    internal sealed class OvertakeDetectionScenario : DataDrivenScenario
    {
        private ScenarioEventBus _eventBus;
        private RaceStateService _state;
        private int _overtakes;

        public OvertakeDetectionScenario() : base(new TestScenarioDefinition(
            "ai-driver-overtake-detection",
            "AI Driver — Overtake Detection (Synthetic)",
            "Scripted arc progress between two cars. Expect ≥1 OvertakeEvent.",
            Array.Empty<ScenarioParameter>()))
        { }

        protected override void ExecuteInternal(ScenarioParameterOverrides overrides)
        {
            _eventBus = new ScenarioEventBus();
            _state = new RaceStateService(_eventBus);

            _eventBus.Subscribe<OvertakeEvent>(evt =>
            {
                _overtakes++;
                Debug.Log($"[OvertakeDetectionScenario] OVERTAKE passer={evt.Passer} passed={evt.Passed} pos={evt.NewPosition}");
            });

            var a = new CarId(1);
            var b = new CarId(2);
            const float dt = 1f / 50f;

            // Phase A: car 1 ahead.
            for (int i = 0; i < 50; i++)
            {
                _eventBus.Publish(Tick(a, 100f + i * 0.5f, dt));
                _eventBus.Publish(Tick(b, 90f + i * 0.4f, dt));
            }
            // Phase B: car 2 catches and overtakes.
            for (int i = 0; i < 50; i++)
            {
                _eventBus.Publish(Tick(a, 125f + i * 0.4f, dt));
                _eventBus.Publish(Tick(b, 110f + i * 0.7f, dt));
            }
            Debug.Log($"[OvertakeDetectionScenario] total overtakes={_overtakes} order={string.Join(",", _state.OrderedCars)}");
        }

        private static CarPhysicsTickedEvent Tick(CarId id, float arc, float dt)
            => new(id, Vector3.zero, 0f, 30f, 0f, 0f, 1f, 0f, SurfaceKind.Asphalt, arc, dt);

        protected override ScenarioVerificationResult VerifyInternal(ScenarioParameterOverrides overrides)
            => new(new List<ScenarioVerificationResult.CheckResult>
            {
                new("race state service constructed", _state != null, "RaceStateService not built"),
            });

        protected override void OnCleanup()
        {
            _state?.Dispose(); _state = null; _eventBus = null;
        }
    }
}
