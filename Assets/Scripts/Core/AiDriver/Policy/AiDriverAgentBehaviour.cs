using UnityPpoRacingTrainer.Core.AiDriver.Physics;
using UnityPpoRacingTrainer.Core.AiDriver.Training;
using Reflex.Extensions;
using Unidad.Core.EventBus;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.AiDriver.Policy
{
    /// <summary>
    /// Canonical ML-Agents shell for the current premium AI driver model.
    /// Currently bound to the 60-float observation layout
    /// (<see cref="RacingObservationLayout"/>, BehaviorName
    /// <c>RacingDriver</c>). Pair with the canonical prefab at
    /// <c>Resources/AiDriver/AiDriverAgent.prefab</c>.
    ///
    /// Version selection (which observation schema, which physics defaults,
    /// which reward shaper) is resolved by <c>AiDriverPolicyService</c> via
    /// <c>IAiDriverVersionProfile</c> — this Behaviour does NOT mutate flags
    /// on the policy service. To bring up a different version, change the
    /// bootstrap's <c>activeVersion</c> field.
    ///
    /// Stays MonoBehaviour because the ML-Agents SDK requires it; all logic
    /// delegates to <see cref="IAiDriverPolicyService"/>.
    /// </summary>
    public sealed class AiDriverAgentBehaviour : Agent, IAiDriverAgentRef
    {
        [SerializeField] private string profileId = "default";
        [SerializeField] private Vector3 spawnPosition;
        [SerializeField] private float spawnHeading;
        [SerializeField] private int gridSlot;

        private IAiDriverPolicyService _policy;
        private IEventBus _eventBus;
        private CarId _carId;
        private bool _registered;

        public void Configure(string profile, Vector3 position, float heading, int slot = 0)
        {
            profileId = profile;
            spawnPosition = position;
            spawnHeading = heading;
            gridSlot = slot;
        }

        public void BindPolicy(IAiDriverPolicyService policy)
        {
            if (_registered && !ReferenceEquals(_policy, policy))
            {
                _policy.UnregisterAgent(_carId);
                _registered = false;
            }
            _policy = policy;
            EnsureRegistered();
        }

        public CarId CarId => _carId;
        public bool IsRegistered => _registered;
        public float LastSteerCmd { get; private set; }
        public float LastThrottleCmd { get; private set; }

        public override void Initialize()
        {
            if (_policy == null || _eventBus == null)
            {
                var container = gameObject.scene.GetSceneContainer();
                _policy ??= container?.Resolve<IAiDriverPolicyService>();
                _eventBus ??= container?.Resolve<IEventBus>();
            }
            EnsureRegistered();
        }

        public override void OnEpisodeBegin()
        {
            if (!_registered) return;
            _policy.BeginEpisode(_carId);
        }

        public override void CollectObservations(VectorSensor sensor)
        {
            if (!_registered) { Pad(sensor); return; }
            _policy.CollectObservations(_carId, sensor);
        }

        public override void OnActionReceived(ActionBuffers actions)
        {
            if (!_registered) return;
            var c = actions.ContinuousActions;
            if (c.Length > 0) LastSteerCmd = c[0];
            if (c.Length > 1) LastThrottleCmd = c[1];
            var result = _policy.ApplyActions(_carId, actions);
            if (result.RewardDelta != 0f) AddReward(result.RewardDelta);
            // Snapshot the running cumulative reward so race telemetry can
            // report a real cum_reward for cars that never receive
            // EpisodeEndedEvent (good drivers lapping indefinitely until the
            // race force-flushes).
            _eventBus?.Publish(new CarRewardSnapshotEvent(_carId, GetCumulativeReward()));
            if (result.End.HasValue) EndEpisode();
        }

        public override void Heuristic(in ActionBuffers actionsOut)
        {
            if (!_registered) return;
            _policy.WriteHeuristicActions(_carId, actionsOut);
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            if (!_registered) return;
            _policy.UnregisterAgent(_carId);
            _registered = false;
        }

        private void EnsureRegistered()
        {
            if (_registered || _policy == null) return;
            _carId = _policy.RegisterAgent(profileId, spawnPosition, spawnHeading);
            _policy.SetGridSlot(_carId, gridSlot);
            _registered = true;
        }

        private static void Pad(VectorSensor sensor)
        {
            for (int i = 0; i < RacingObservationLayout.FloatsPerFrame; i++)
                sensor.AddObservation(0f);
        }
    }
}
