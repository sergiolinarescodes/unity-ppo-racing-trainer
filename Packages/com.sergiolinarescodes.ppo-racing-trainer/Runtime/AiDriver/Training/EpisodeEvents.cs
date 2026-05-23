using UnityPpoRacingTrainer.Core.AiDriver.Physics;

namespace UnityPpoRacingTrainer.Core.AiDriver.Training
{
    /// <summary>
    /// Published once when an episode terminates for any reason. Carries
    /// enough state for HUD readout, replay tagging, and curriculum
    /// bookkeeping without forcing subscribers to query the controller.
    /// </summary>
    /// <param name="Car">Car identifier whose episode ended.</param>
    /// <param name="Reason">Why the episode ended (see <see cref="EpisodeEndReason"/>).</param>
    /// <param name="CumulativeReward">Sum of all reward deltas paid this episode.</param>
    /// <param name="Steps">Number of decision steps elapsed.</param>
    /// <param name="ElapsedSec">Wall-clock seconds elapsed since episode start.</param>
    /// <param name="LapsCompleted">Lap count credited this episode (0..N).</param>
    public readonly record struct EpisodeEndedEvent(
        CarId Car,
        EpisodeEndReason Reason,
        float CumulativeReward,
        int Steps,
        float ElapsedSec,
        int LapsCompleted);

    /// <summary>
    /// Published every decision step from the agent shell after AddReward, so
    /// passive listeners (race telemetry, HUD) can see the running cumulative
    /// reward without waiting for episode end. Required because good drivers
    /// lap indefinitely and never fire EpisodeEndedEvent, leaving the race
    /// record's cum_reward field zero when the race force-flushes.
    /// </summary>
    public readonly record struct CarRewardSnapshotEvent(
        CarId Car,
        float Cumulative);
}
