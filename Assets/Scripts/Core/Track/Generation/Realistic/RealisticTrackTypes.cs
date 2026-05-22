using UnityPpoRacingTrainer.Core.AiDriver.Training.Curriculum;
using UnityPpoRacingTrainer.Core.AiDriver.Training.Generation;
using Unidad.Core.Grid;

namespace UnityPpoRacingTrainer.Core.Track.Generation.Realistic
{
    /// <summary>
    /// F1-style generator inputs. Wraps the existing <see cref="GenerationConfig"/> +
    /// stage so the realistic generator can be called with the same parameters as
    /// the recipe-based one. The post-rewrite generator drives a Burst beam search
    /// over the player's full shape catalog; the additional knobs below tune that
    /// search.
    /// </summary>
    public readonly record struct RealisticTrackGenerationConfig(
        int Seed,
        GridPosition Origin,
        TrackDirection InitialFacing,
        CurriculumStage Stage,
        int MinMainStraightCells = 7,
        int MaxMainStraightCells = 15,
        int RecipeAttempts = 6,
        bool AllowDiagonalSweeps = true,
        // Search-driven knobs (post-rewrite)
        int TargetLengthCells = 60,
        int TargetLengthTolerance = 20,
        float TurnDensity = 0.4f,
        int MaxConsecutiveStraights = 3,
        float CornerSeverityBias = 0.5f,
        bool RequireRamps = false,
        int ClosureSearchRadius = 8,
        int BeamWidth = 16,
        int MaxSearchSteps = 32,
        int PerAttemptTimeoutMs = 1500)
    {
        public static RealisticTrackGenerationConfig From(in GenerationConfig cfg) =>
            new(cfg.Seed, cfg.Origin, cfg.InitialFacing, cfg.Stage);
    }

    /// <summary>
    /// Marker contract: the F1-flavoured generator. Extends the existing
    /// <see cref="IProceduralLoopGenerator"/> so consumers can resolve it as
    /// either type. Adds an explicit overload for callers that want the
    /// realistic-specific knobs.
    /// </summary>
    public interface IRealisticTrackGenerator : IProceduralLoopGenerator
    {
        GenerationResult Generate(in RealisticTrackGenerationConfig cfg);
    }
}
