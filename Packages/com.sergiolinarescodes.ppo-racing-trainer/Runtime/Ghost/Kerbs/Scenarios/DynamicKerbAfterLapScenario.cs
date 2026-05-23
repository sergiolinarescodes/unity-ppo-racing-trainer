using System;
using System.Collections.Generic;
using Unidad.Core.Testing;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Ghost.Kerbs.Scenarios
{
    /// <summary>
    /// Sanity scenario for the dynamic racing-line kerb event shape. The real
    /// service runs against live ghost + collision services in the Bootstrap
    /// scene — end-to-end coverage lives in Play-mode. This scenario exists so
    /// AllSystemScenariosTests has a deterministic fact to check.
    /// </summary>
    internal sealed class DynamicKerbAfterLapScenario : DataDrivenScenario
    {
        public DynamicKerbAfterLapScenario() : base(new TestScenarioDefinition(
            "dynamic-kerb-after-lap",
            "Dynamic Kerb — Event Shape",
            "Verifies the DynamicKerbSpawnedEvent + DynamicKerbsClearedEvent struct shape used by the racing-line kerb service.",
            Array.Empty<ScenarioParameter>()))
        { }

        protected override void ExecuteInternal(ScenarioParameterOverrides overrides)
        {
            // Compose the event structs to make sure the public surface is stable.
            var spawn = new DynamicKerbSpawnedEvent(
                Track.TrackPieceId.New(), new Vector3(1f, 0f, 1f), +1);
            var clear = new DynamicKerbsClearedEvent("test");
            Debug.Log($"[DynamicKerbScenario] spawn side={spawn.Side} clear reason={clear.Reason}");
        }

        protected override ScenarioVerificationResult VerifyInternal(ScenarioParameterOverrides overrides)
        {
            return new ScenarioVerificationResult(new List<ScenarioVerificationResult.CheckResult>
            {
                new("DynamicKerbSpawnedEvent is a struct",
                    typeof(DynamicKerbSpawnedEvent).IsValueType,
                    "event must be a struct (event-bus contract)"),
                new("DynamicKerbsClearedEvent is a struct",
                    typeof(DynamicKerbsClearedEvent).IsValueType,
                    "event must be a struct (event-bus contract)"),
            });
        }

        protected override void OnCleanup() { }
    }
}
