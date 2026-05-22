using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.Terrain.Scenarios;
using Unidad.Core.Testing;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Track.Generation.StarterStrip.Scenarios
{
    /// <summary>
    /// Smoke scenario: subscribes to <see cref="StarterStripGeneratedEvent"/>
    /// on the scenario bus. The real generator runs against the live
    /// <see cref="ITrackPlacementService"/>; this scenario only proves the
    /// event-shape and Verify pipeline. End-to-end coverage lives in the
    /// orchestrator's integration scenario.
    /// </summary>
    internal sealed class StarterStripGenerationScenario : DataDrivenScenario
    {
        private ScenarioEventBus _bus;
        private int _generated;
        private IDisposable _sub;

        public StarterStripGenerationScenario() : base(new TestScenarioDefinition(
            "starter-strip-generation",
            "Starter Strip — Synthetic Generation Event",
            "Publishes a synthetic StarterStripGeneratedEvent and asserts the subscriber sees it.",
            Array.Empty<ScenarioParameter>()))
        { }

        protected override void ExecuteInternal(ScenarioParameterOverrides overrides)
        {
            _bus = new ScenarioEventBus();
            _sub = _bus.Subscribe<StarterStripGeneratedEvent>(evt =>
            {
                _generated++;
                Debug.Log($"[StarterStripScenario] octant={evt.Octant} pieces={evt.PieceCount} start={evt.StartLineWorldPos} heading={evt.StartHeading:F3}rad");
            });
            _bus.Publish(new StarterStripGeneratedEvent(3, 8, new Vector3(0f, 0f, 0f), 0f));
        }

        protected override ScenarioVerificationResult VerifyInternal(ScenarioParameterOverrides overrides)
            => new(new List<ScenarioVerificationResult.CheckResult>
            {
                new("synthetic event observed", _generated == 1, $"expected 1 event, got {_generated}"),
            });

        protected override void OnCleanup()
        {
            _sub?.Dispose(); _sub = null;
            _bus = null;
        }
    }
}
