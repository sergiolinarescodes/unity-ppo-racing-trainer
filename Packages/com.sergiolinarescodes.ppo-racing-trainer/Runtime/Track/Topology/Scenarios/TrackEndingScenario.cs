using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.Terrain.Scenarios;
using Unidad.Core.Testing;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Track.Topology.Scenarios
{
    /// <summary>
    /// Smoke scenario: subscribes to <see cref="TrackTopologyChangedEvent"/> on
    /// the scenario bus and logs each fire so the deduper behaviour is visible
    /// in the Console while the player or another scenario lays/removes pieces.
    /// Verify() asserts deterministic setup only — actual topology changes are
    /// triggered by interactive or upstream scenarios.
    /// </summary>
    internal sealed class TrackEndingScenario : DataDrivenScenario
    {
        private ScenarioEventBus _bus;
        private int _changes;
        private IDisposable _sub;

        public TrackEndingScenario() : base(new TestScenarioDefinition(
            "track-topology-ending",
            "Track Topology — Open Ends Listener",
            "Logs every TrackTopologyChangedEvent on the scenario bus.",
            Array.Empty<ScenarioParameter>()))
        { }

        protected override void ExecuteInternal(ScenarioParameterOverrides overrides)
        {
            _bus = new ScenarioEventBus();
            _sub = _bus.Subscribe<TrackTopologyChangedEvent>(evt =>
            {
                _changes++;
                Debug.Log($"[TrackEndingScenario] topology change openEnds={evt.OpenEndCount} closed={evt.IsClosedLoop} (total={_changes})");
            });

            // Synthetic fire so the scenario emits at least one log on Execute.
            _bus.Publish(new TrackTopologyChangedEvent(0, false));
        }

        protected override ScenarioVerificationResult VerifyInternal(ScenarioParameterOverrides overrides)
            => new(new List<ScenarioVerificationResult.CheckResult>
            {
                new("scenario bus constructed", _bus != null, "ScenarioEventBus null"),
                new("subscribed to changes", _sub != null, "TrackTopologyChangedEvent subscription failed"),
                new("at least one synthetic fire observed", _changes >= 1, $"expected ≥1 change events, got {_changes}"),
            });

        protected override void OnCleanup()
        {
            _sub?.Dispose(); _sub = null;
            _bus = null;
        }
    }
}
