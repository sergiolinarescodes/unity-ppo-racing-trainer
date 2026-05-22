using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.AiDriver.Physics;
using UnityPpoRacingTrainer.Core.AiDriver.Training;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.AiDriver.Policy
{
    /// <summary>
    /// Per-car policy backend. The MonoBehaviour shell
    /// (<see cref="AiDriverAgentBehaviour"/>) owns the ML-Agents <c>Agent</c> contract;
    /// it routes observation collection / action receipt to this service so the
    /// reward, episode, and observation logic stay pure C# and unit-testable.
    /// </summary>
    public interface IAiDriverPolicyService
    {
        /// <summary>Spawn a car for the agent and tag it with a profile. Returns the car id.</summary>
        CarId RegisterAgent(string profileId, Vector3 spawnPosition, float heading);

        /// <summary>Reset the agent's car to lap-start + clear episode state.</summary>
        void BeginEpisode(CarId carId);

        /// <summary>Update the spawn pose used by the next <see cref="BeginEpisode"/>.
        /// Called by <c>TrainingDirector</c> after a procedural loop is regenerated so
        /// the next ML-Agents reset puts the car at the new lap-start.</summary>
        void SetSpawnPose(CarId carId, Vector3 spawnPosition, float heading);

        /// <summary>
        /// Force ALL registered agents to spawn at the given pose on their next
        /// <see cref="BeginEpisode"/>, bypassing per-agent random anchor selection
        /// for one episode. Called by <c>TrainingDirector</c> right after a circuit
        /// regenerates so every car starts at the new circuit's start line.
        /// </summary>
        void RespawnAllAtStartLine(Vector3 spawnPosition, float heading);

        /// <summary>Snapshot of the active driver profile for an agent.</summary>
        DriverProfileSnapshot GetProfile(CarId carId);

        /// <summary>
        /// Assign a grid slot (0 = pole, 1+ staggered back). Applied on every
        /// <see cref="BeginEpisode"/> as a (longitudinal, lateral) offset from the
        /// canonical lap-start pose so multi-agent training doesn't pile every
        /// car on the same coordinate — required once <c>CarCollisionService</c>
        /// is in the loop. Pass 0 for the canonical pose (back-compat default).
        /// </summary>
        void SetGridSlot(CarId carId, int slot);

        /// <summary>Fill the VectorSensor with the canonical observation layout.</summary>
        void CollectObservations(CarId carId, VectorSensor sensor);

        /// <summary>
        /// Returns the most recent 25-float observation vector that was fed
        /// to the policy network for this car (cached during
        /// <see cref="CollectObservations"/>), or null if none has been
        /// computed yet. Read-only — do not mutate the returned list.
        /// Used by the trainer Inspector telemetry to show the exact policy
        /// view alongside the raw physics state.
        /// </summary>
        IReadOnlyList<float> GetLastObservation(CarId carId);

        /// <summary>Map ActionBuffers → DriverInput, apply via the car simulation, then
        /// pull the per-step reward + end signal from the active reward source.
        /// The agent shell calls <c>AddReward</c> / <c>EndEpisode</c> on the result.</summary>
        StepResult ApplyActions(CarId carId, in ActionBuffers actions);

        /// <summary>Heuristic source — pure-pursuit fallback when no model is bound.</summary>
        void WriteHeuristicActions(CarId carId, in ActionBuffers actionsOut);

        /// <summary>Tear down the car when an agent is being disposed.</summary>
        void UnregisterAgent(CarId carId);

        /// <summary>Swap the reward source. The training installer calls this once at
        /// bootstrap; tests can rebind to a stub. Pass <c>null</c> to fall back to
        /// the no-op <see cref="NullEpisodeRewardSource"/>.</summary>
        void RegisterRewardSource(IEpisodeRewardSource source);
    }
}
