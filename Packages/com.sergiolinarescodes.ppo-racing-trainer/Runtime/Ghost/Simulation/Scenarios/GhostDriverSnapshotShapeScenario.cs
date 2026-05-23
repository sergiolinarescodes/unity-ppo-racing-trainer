using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.AiDriver.Physics;
using Unidad.Core.Testing;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Ghost.Simulation.Scenarios
{
    /// <summary>
    /// Snapshot-shape smoke: builds a synthetic GhostSimSnapshot and verifies
    /// the record layout. Full driving end-to-end runs in the director's
    /// integration scenario where the live CarSimulationService is bound.
    /// </summary>
    internal sealed class GhostDriverSnapshotShapeScenario : DataDrivenScenario
    {
        private GhostSimSnapshot _snap;

        public GhostDriverSnapshotShapeScenario() : base(new TestScenarioDefinition(
            "ghost-driver-snapshot-shape",
            "Ghost Driver — Snapshot Record Shape",
            "Constructs a GhostSimSnapshot with known fields and asserts read-back equality.",
            Array.Empty<ScenarioParameter>()))
        { }

        protected override void ExecuteInternal(ScenarioParameterOverrides overrides)
        {
            _snap = new GhostSimSnapshot(
                Position: new Vector3(1.5f, 0f, 2.5f),
                Heading: Mathf.PI * 0.25f,
                Speed: 6.2f,
                IsOffTrack: false,
                LapsCompleted: 2,
                BodyLeanRad: 0.1f);
            Debug.Log($"[GhostDriverSnapshotShapeScenario] snap pos={_snap.Position} heading={_snap.Heading:F3} speed={_snap.Speed:F2} laps={_snap.LapsCompleted}");
        }

        protected override ScenarioVerificationResult VerifyInternal(ScenarioParameterOverrides overrides)
            => new(new List<ScenarioVerificationResult.CheckResult>
            {
                new("position read-back", _snap.Position == new Vector3(1.5f, 0f, 2.5f), "position mismatch"),
                new("laps read-back", _snap.LapsCompleted == 2, "laps mismatch"),
                new("off-track default false", !_snap.IsOffTrack, "off-track flag wrong"),
            });

        protected override void OnCleanup() { _snap = default; }
    }
}
