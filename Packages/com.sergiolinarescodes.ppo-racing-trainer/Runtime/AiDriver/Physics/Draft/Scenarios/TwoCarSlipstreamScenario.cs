using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.Terrain.Scenarios;
using UnityPpoRacingTrainer.Core.Track;
using Unidad.Core.Testing;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.AiDriver.Physics.Draft.Scenarios
{
    /// <summary>
    /// Synthetic two-car slipstream. Leader at +6m ahead, follower closing in
    /// over 4 seconds. Watch the smoothed draft strength rise toward 1.0, then
    /// the follower laterally exits the wake — draft decays slowly (asymmetric
    /// release) so the speed bonus carries into the pass.
    /// </summary>
    internal sealed class TwoCarSlipstreamScenario : DataDrivenScenario
    {
        private ScenarioEventBus _eventBus;
        private DraftService _draft;
        private CarId _follower;
        private CarId _leader;

        public TwoCarSlipstreamScenario() : base(new TestScenarioDefinition(
            "ai-driver-two-car-slipstream",
            "AI Driver — Two-Car Slipstream (Synthetic)",
            "Scripts a follower closing on a leader and logs smoothed draft strength.",
            Array.Empty<ScenarioParameter>()))
        { }

        protected override void ExecuteInternal(ScenarioParameterOverrides overrides)
        {
            _eventBus = new ScenarioEventBus();
            _draft = new DraftService(_eventBus);
            _follower = new CarId(1);
            _leader = new CarId(2);

            _eventBus.Subscribe<DraftStateChangedEvent>(evt =>
            {
                if (evt.Id.Value == _follower.Value)
                    Debug.Log($"[TwoCarSlipstreamScenario] draft={evt.Strength:F3} leader={evt.LeaderId}");
            });

            const float dt = 1f / 50f;
            // Phase 1: closing the gap (4 s).
            float gap = 6f;
            for (int i = 0; i < 50 * 4; i++)
            {
                gap = Mathf.Max(1.5f, gap - 1.0f * dt);
                Publish(_leader, new Vector3(0f, 0f, gap));
                Publish(_follower, Vector3.zero);
            }
            // Phase 2: pull out to the side over 1 second (gap held).
            for (int i = 0; i < 50; i++)
            {
                float lateral = (i / 50f) * 3f;
                Publish(_leader, new Vector3(0f, 0f, gap));
                Publish(_follower, new Vector3(lateral, 0f, 0f));
            }

            var state = _draft.Get(_follower);
            Debug.Log($"[TwoCarSlipstreamScenario] Final follower draft strength={state.Strength:F3} leader={state.LeaderId}");
        }

        private void Publish(CarId id, Vector3 pos)
        {
            _eventBus.Publish(new CarPhysicsTickedEvent(
                id, pos, 0f, 50f,
                LateralAcceleration: 0f, Slip: 0f, ThrottleInput: 1f, BrakeInput: 0f,
                Surface: SurfaceKind.Asphalt, ArcLengthAlong: 0f, Dt: 1f / 50f));
        }

        protected override ScenarioVerificationResult VerifyInternal(ScenarioParameterOverrides overrides)
            => new(new List<ScenarioVerificationResult.CheckResult>
            {
                new("draft service constructed", _draft != null, "DraftService not built"),
            });

        protected override void OnCleanup()
        {
            _draft?.Dispose(); _draft = null; _eventBus = null;
        }
    }
}
