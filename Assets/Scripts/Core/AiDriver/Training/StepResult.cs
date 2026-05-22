namespace UnityPpoRacingTrainer.Core.AiDriver.Training
{
    /// <summary>
    /// Per-decision tick output of the reward source. The MonoBehaviour shell
    /// (<c>AiDriverAgentBehaviour</c>) calls <c>AddReward(RewardDelta)</c> and,
    /// when <see cref="End"/> has a value, <c>EndEpisode()</c>. Default value is
    /// (0, null) so a null-object reward source is the same as "no training."
    /// </summary>
    public readonly record struct StepResult(float RewardDelta, EpisodeEndReason? End)
    {
        public static StepResult None => default;
    }
}
