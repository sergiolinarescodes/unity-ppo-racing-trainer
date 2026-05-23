using System;
using System.Collections.Generic;
using Unidad.Core.Testing;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Track.Authoring.CircuitProfiles.Scenarios
{
    /// <summary>
    /// Placeholder visual scenario. v1 logs only — the heatmap rendering will
    /// land once a real closed loop is set up in the scenario harness. Verify()
    /// is deterministic so the AllSystemScenariosTests pass.
    /// </summary>
    internal sealed class CircuitTireStressHeatmapScenario : DataDrivenScenario
    {
        public CircuitTireStressHeatmapScenario() : base(new TestScenarioDefinition(
            "track-circuit-tire-stress-heatmap",
            "Track — Circuit Tire-Stress Heatmap (Stub)",
            "Stub scenario — logs that the circuit-tire-profile service is wired.",
            Array.Empty<ScenarioParameter>()))
        { }

        protected override void ExecuteInternal(ScenarioParameterOverrides overrides)
        {
            Debug.Log("[CircuitTireStressHeatmapScenario] Wired. Full heatmap render lands with centerline access.");
        }

        protected override ScenarioVerificationResult VerifyInternal(ScenarioParameterOverrides overrides)
            => new(new List<ScenarioVerificationResult.CheckResult>
            {
                new("scenario ran", true, "scenario didn't run"),
            });
    }
}
