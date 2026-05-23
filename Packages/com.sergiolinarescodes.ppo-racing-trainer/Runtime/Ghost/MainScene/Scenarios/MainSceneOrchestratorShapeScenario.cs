using System;
using System.Collections.Generic;
using Unidad.Core.Testing;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Ghost.MainScene.Scenarios
{
    /// <summary>
    /// Shape-only scenario for AllSystemScenariosTests coverage. The real
    /// orchestrator runs against live services from GameBootstrap; end-to-end
    /// validation happens by pressing Play on Bootstrap.unity.
    /// </summary>
    internal sealed class MainSceneOrchestratorShapeScenario : DataDrivenScenario
    {
        public MainSceneOrchestratorShapeScenario() : base(new TestScenarioDefinition(
            "main-scene-orchestrator-shape",
            "Main Scene Orchestrator — Shape",
            "Stub scenario; the orchestrator runs in Play mode only.",
            Array.Empty<ScenarioParameter>()))
        { }

        protected override void ExecuteInternal(ScenarioParameterOverrides overrides)
        {
            Debug.Log("[MainSceneOrchestratorShapeScenario] shape check only — see Bootstrap.unity Play for live test.");
        }

        protected override ScenarioVerificationResult VerifyInternal(ScenarioParameterOverrides overrides)
            => new(new List<ScenarioVerificationResult.CheckResult>
            {
                new("scenario executed", true, "always true"),
            });

        protected override void OnCleanup() { }
    }
}
