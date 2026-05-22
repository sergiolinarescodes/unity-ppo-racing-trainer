using UnityPpoRacingTrainer.Core.AiDriver.Training.Curriculum;
using UnityPpoRacingTrainer.Core.Track;
using Unidad.Core.Grid;

namespace UnityPpoRacingTrainer.Core.AiDriver.Training.Generation
{
    /// <summary>
    /// Inputs to <see cref="IProceduralLoopGenerator.Generate"/>. Same seed +
    /// same stage + same starting cell + facing → same loop, every time.
    /// </summary>
    /// <param name="Seed">
    /// RNG seed for piece selection and recipe parameter sampling.
    /// UP / DOWN: deterministic — changing the seed produces a different loop
    /// at the same origin / facing / stage. Drives reproducibility for tests
    /// and dataset regeneration.
    /// </param>
    /// <param name="Origin">
    /// Starting grid cell for the first piece of the loop.
    /// UP / DOWN: spatial offset only — translates the entire generated loop.
    /// Closure is still guaranteed by the recipe.
    /// </param>
    /// <param name="InitialFacing">
    /// Heading the first piece is laid in (N/S/E/W).
    /// UP / DOWN: rotates the entire loop. Pair with <see cref="Origin"/> to
    /// place the loop anywhere on the grid without changing its shape.
    /// </param>
    /// <param name="Stage">
    /// Curriculum stage driving recipe weights, length bounds, and lap-time
    /// target. See <see cref="CurriculumStage"/>.
    /// UP (higher stage id): harder loops — more corners, longer length budget.
    /// DOWN: warm-up loops with simpler topology.
    /// </param>
    public readonly record struct GenerationConfig(
        int Seed,
        GridPosition Origin,
        TrackDirection InitialFacing,
        CurriculumStage Stage);
}
