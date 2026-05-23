using UnityPpoRacingTrainer.Core.AiDriver.Physics;

namespace UnityPpoRacingTrainer.Core.AiDriver.Training
{
    /// <summary>
    /// Per-decision-tick reward + end-of-episode source. The policy service
    /// holds one instance and calls it from <c>ApplyActions</c>; the result
    /// feeds back to the ML-Agents <c>Agent</c> shell which performs the SDK
    /// calls (<c>AddReward</c>, <c>EndEpisode</c>).
    ///
    /// Implementations:
    /// <list type="bullet">
    /// <item><see cref="NullEpisodeRewardSource"/> — no-op for inference / heuristic eval.</item>
    /// <item><see cref="EpisodeRunner"/> — accumulates reward + checks end conditions during training.</item>
    /// <item><see cref="CompositeEpisodeRewardSource"/> — base source plus the personality-aware <see cref="RewardShaper"/>.</item>
    /// </list>
    /// </summary>
    public interface IEpisodeRewardSource
    {
        /// <summary>Called when the agent's episode begins (after car teleport).</summary>
        void OnEpisodeBegin(CarId carId);

        /// <summary>Called once per ML decision tick after the action is applied.
        /// Returns the reward delta to add and, if non-null, the reason this episode
        /// must end now.</summary>
        StepResult PostStep(CarId carId);

        /// <summary>Called when the agent is being torn down (scene cleanup, scenario reset).</summary>
        void OnAgentUnregistered(CarId carId);
    }
}
