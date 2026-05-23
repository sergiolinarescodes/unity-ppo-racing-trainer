using System;
using System.Collections.Generic;
using Unidad.Core.Testing;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Ghost.Director.Scenarios
{
    /// <summary>
    /// Sanity scenario for the enum-shape of <see cref="GhostDirectorState"/>.
    /// The real director runs against live services in the Bootstrap scene,
    /// so end-to-end coverage lives in Play-mode rather than NUnit. This
    /// scenario exists so AllSystemScenariosTests has a fact to check.
    /// </summary>
    internal sealed class GhostDirectorStateShapeScenario : DataDrivenScenario
    {
        private GhostDirectorState _seen;

        public GhostDirectorStateShapeScenario() : base(new TestScenarioDefinition(
            "ghost-director-state-shape",
            "Ghost Director — State Enum Shape",
            "Verifies the GhostDirectorState enum has the expected idle/spawn/settle/drive/respawn values.",
            Array.Empty<ScenarioParameter>()))
        { }

        protected override void ExecuteInternal(ScenarioParameterOverrides overrides)
        {
            _seen = GhostDirectorState.Drive;
            Debug.Log($"[GhostDirectorStateShapeScenario] state={_seen}");
        }

        protected override ScenarioVerificationResult VerifyInternal(ScenarioParameterOverrides overrides)
        {
            var values = Enum.GetValues(typeof(GhostDirectorState));
            bool hasIdle = false, hasDrop = false, hasSettle = false, hasDrive = false, hasResp = false;
            foreach (GhostDirectorState v in values)
            {
                if (v == GhostDirectorState.Idle) hasIdle = true;
                if (v == GhostDirectorState.SpawnDrop) hasDrop = true;
                if (v == GhostDirectorState.Settle) hasSettle = true;
                if (v == GhostDirectorState.Drive) hasDrive = true;
                if (v == GhostDirectorState.Respawn) hasResp = true;
            }
            return new ScenarioVerificationResult(new List<ScenarioVerificationResult.CheckResult>
            {
                new("Idle present", hasIdle, "missing Idle"),
                new("SpawnDrop present", hasDrop, "missing SpawnDrop"),
                new("Settle present", hasSettle, "missing Settle"),
                new("Drive present", hasDrive, "missing Drive"),
                new("Respawn present", hasResp, "missing Respawn"),
                new("execute set state to Drive", _seen == GhostDirectorState.Drive, "execute did not set state"),
            });
        }

        protected override void OnCleanup() { }
    }
}
