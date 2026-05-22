namespace UnityPpoRacingTrainer.Core.AiDriver.Training.Curriculum
{
    /// <summary>
    /// Immutable description of one curriculum step. Fed into
    /// <see cref="Generation.IProceduralLoopGenerator"/> to produce a training loop
    /// of the appropriate difficulty. Pure data — no DI registration.
    /// Weights are interpreted relatively by the piece picker (the sum need not
    /// equal 1); see <see cref="Generation.LoopBuilder.PickPiece"/>.
    /// </summary>
    /// <param name="Id">
    /// Stable integer key for the stage. Used by the policy service and by
    /// ML-Agents' <c>EnvironmentParameters.stage_id</c> lesson switch.
    /// </param>
    /// <param name="Name">
    /// Human-readable label (e.g. "Warmup", "Easy", "Hard", "Authored"). Used in
    /// HUD overlays and telemetry — never parsed by code.
    /// </param>
    /// <param name="StraightWeight">
    /// Relative likelihood the piece picker emits a straight segment.
    /// UP: longer straights, fewer corners — easier loops, faster lap times,
    /// less steering practice for the policy.
    /// DOWN: corners become dominant — loops twist more, exposing the policy
    /// to braking / cornering decisions.
    /// </param>
    /// <param name="RightCurveWeight">
    /// Relative likelihood of a right-hand curve piece.
    /// UP: loops bias to the right — useful when balancing turn direction
    /// against <see cref="LeftCurveWeight"/>.
    /// DOWN: right curves become rare; if both curve weights are tiny the
    /// generator produces near-rectangular loops.
    /// </param>
    /// <param name="LeftCurveWeight">
    /// Relative likelihood of a left-hand curve piece. Mirror role of
    /// <see cref="RightCurveWeight"/>.
    /// UP / DOWN: same effect on left turns.
    /// </param>
    /// <param name="RampWeight">
    /// Relative likelihood of an inclined / ramp piece. Currently 0 for every
    /// stage — ramps are reserved for future training.
    /// UP: loops gain elevation changes — policy must learn ramp launches.
    /// DOWN (0): loops are pure planar; gravity and ramp physics are exercised
    /// only via off-track terrain.
    /// </param>
    /// <param name="MinPieces">
    /// Lower bound on the piece count emitted by the generator before closure
    /// is considered.
    /// UP: forces longer minimum loops — more variety, longer episodes, harder
    /// to overfit short circuits.
    /// DOWN: short loops dominate — quick episode cycles, faster reward
    /// gradient but risk of memorizing a tight set of layouts.
    /// </param>
    /// <param name="MaxPieces">
    /// Upper bound on the piece count emitted. Generation aborts past this.
    /// UP: huge loops become possible — longer episodes, more time-horizon
    /// pressure on PPO, higher episode-length variance.
    /// DOWN: caps loop size — keeps episode duration predictable.
    /// </param>
    /// <param name="MinPiecesBeforeClose">
    /// Number of pieces required before the closure detector is allowed to fire.
    /// Prevents the generator from snapping the loop shut after only a few cells.
    /// UP: forces the generator to commit to longer layouts before snapping shut.
    /// DOWN: tiny loops become legal — useful for warmup, dangerous in late stages
    /// because the policy can solve them by accident.
    /// </param>
    /// <param name="MaxBacktracks">
    /// Cap on the number of search backtracks the generator may perform before
    /// giving up on the current attempt.
    /// UP: more time spent on tough layouts — gets stage-4 authored circuits
    /// through but burns CPU per episode reset.
    /// DOWN: generator gives up quickly and falls back to the deterministic
    /// stadium — keeps trainer responsive on warmup stages.
    /// </param>
    /// <param name="TargetLapTimeSeconds">
    /// Pacing target consumed by generators for length-budget heuristics and by
    /// telemetry / episode-end reward sources.
    /// UP: generators bias toward longer / faster loops — lap-time-window
    /// bonuses shift outward.
    /// DOWN: loops are tuned for tight, sub-15-second laps — favours warmup
    /// circuits where the policy can complete many episodes per minute.
    /// </param>
    public readonly record struct CurriculumStage(
        int Id,
        string Name,
        float StraightWeight,
        float RightCurveWeight,
        float LeftCurveWeight,
        float RampWeight,
        int MinPieces,
        int MaxPieces,
        int MinPiecesBeforeClose,
        int MaxBacktracks,
        float TargetLapTimeSeconds);
}
