using System;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Track.Scenarios
{
    /// <summary>
    /// Scenario-only MonoBehaviour that drives the scenario's <c>ITickable.Tick</c>
    /// from <c>MonoBehaviour.Update</c>. The runtime <c>TickRunner</c> isn't available
    /// inside scenarios (Scenario Browser builds its own service graph), so this
    /// proxy is the single allowed <c>Time.deltaTime</c> read in the placement flow.
    /// </summary>
    internal sealed class MouseShapePlacementTickProxy : MonoBehaviour
    {
        public Action<float> OnTick;

        private void Update()
        {
            OnTick?.Invoke(Time.deltaTime);
        }
    }
}
