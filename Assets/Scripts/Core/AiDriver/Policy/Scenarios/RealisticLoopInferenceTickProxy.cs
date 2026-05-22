using System;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.AiDriver.Policy.Scenarios
{
    /// <summary>
    /// Tick relay for <see cref="RealisticLoopInferenceScenario"/>. Marked
    /// <see cref="ExecuteAlways"/> so Update / FixedUpdate fire in Edit mode too —
    /// the Scenario Browser executes scenarios in Edit mode, and the heuristic-only
    /// tick proxy stalls until the user nudges the scene to force a refresh.
    /// </summary>
    [ExecuteAlways]
    internal sealed class RealisticLoopInferenceTickProxy : MonoBehaviour
    {
        public Action<float> OnUpdate;
        public Action<float> OnFixedUpdate;

        private void Update()
        {
            // In Edit mode Time.deltaTime is 0 on the first frame; fall back to a
            // fixed step so the policy decides at a stable cadence.
            float dt = Application.isPlaying ? Time.deltaTime : 1f / 60f;
            OnUpdate?.Invoke(dt);
        }

        private void FixedUpdate()
        {
            // Same Time.fixedDeltaTime fallback for Edit-mode FixedUpdate (which
            // Unity does still pump on [ExecuteAlways] components, just less often).
            float dt = Application.isPlaying ? Time.fixedDeltaTime : 1f / 50f;
            OnFixedUpdate?.Invoke(dt);
        }
    }
}
