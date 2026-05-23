namespace UnityPpoRacingTrainer.Core.AiDriver.Training
{
    /// <summary>
    /// Why an episode terminated. Used both for telemetry labelling and for
    /// per-reason terminal-reward shaping in the reward calculator.
    /// <see cref="Aborted"/> means "drop the trajectory" — produced when the
    /// loop service signals the closed loop has opened mid-episode and the
    /// reward record is no longer trustworthy.
    /// </summary>
    public enum EpisodeEndReason
    {
        /// <summary>Lap completed (or, in multi-lap mode, at least one full lap before the step cap).</summary>
        Success,

        /// <summary>Agent stayed off the track surface beyond the off-track timeout.</summary>
        Failure_OffTrack,

        /// <summary>Agent ran out of decision steps without completing a lap.</summary>
        Failure_Timeout,

        /// <summary>Backstop: per-episode wall-hit cap reached without a wreck.</summary>
        Failure_WallHit,

        /// <summary>
        /// Chassis health reached zero from accumulated wall-impact damage.
        /// Damage scales with impact speed squared, so high-speed crashes
        /// wreck instantly and low-speed scrapes accrue slowly. Primary
        /// crash fail mode; <see cref="Failure_WallHit"/> stays as a backstop.
        /// </summary>
        Failure_Wreck,

        /// <summary>
        /// Tank drained to zero before crossing the next start line. The fuel
        /// service forces coast at 0 L; this terminates the episode so the
        /// trainer does not waste ticks on a stranded car.
        /// </summary>
        Failure_FuelOut,

        /// <summary>
        /// Both tires punctured AND the car ran off the track. A single
        /// puncture stays drivable so the policy can learn to limp home;
        /// double puncture + off-track is unrecoverable.
        /// </summary>
        Failure_PuncturedAndOffTrack,

        /// <summary>Episode trajectory invalidated mid-run (e.g. the loop opened). Drop from training data.</summary>
        Aborted,
    }
}
