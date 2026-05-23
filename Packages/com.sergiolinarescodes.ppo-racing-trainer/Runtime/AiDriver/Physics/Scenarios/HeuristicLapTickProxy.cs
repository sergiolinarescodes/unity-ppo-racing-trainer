using System;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.AiDriver.Physics.Scenarios
{
    /// <summary>
    /// Scenario-only MonoBehaviour. The Scenario Browser builds its own service
    /// graph without spawning a TickRunner, so this proxy is the lone Time-source
    /// for the heuristic lap scenario: Update drives the policy + HUD; FixedUpdate
    /// drives <see cref="ICarSimulationService.FixedTick"/> through the scenario.
    /// </summary>
    internal sealed class HeuristicLapTickProxy : MonoBehaviour
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
