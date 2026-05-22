using System;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.AiDriver.Policy.Scenarios
{
    /// <summary>
    /// Tiny tick relay for <see cref="MlAgentHeuristicLapScenario"/>. Mirrors
    /// HeuristicLapTickProxy — the scenario owns the service graph, this MB just
    /// pipes Update / FixedUpdate so the car sim keeps stepping while the scenario
    /// is active in the editor.
    /// </summary>
    internal sealed class MlAgentHeuristicLapTickProxy : MonoBehaviour
    {
        public Action<float> OnUpdate;
        public Action<float> OnFixedUpdate;

        private void Update()
        {
            OnUpdate?.Invoke(Time.deltaTime);
        }

        private void FixedUpdate()
        {
            OnFixedUpdate?.Invoke(Time.fixedDeltaTime);
        }
    }
}
