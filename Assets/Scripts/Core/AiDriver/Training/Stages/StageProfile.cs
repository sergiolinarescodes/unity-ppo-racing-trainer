using System;

namespace UnityPpoRacingTrainer.Core.AiDriver.Training.Stages
{
    /// <summary>
    /// Stage-conditional feature flags. Each bit gates one reward term, one
    /// observation block, or one terminal penalty. The full set of bits active
    /// for a given <c>stage_id</c> is declared by an <see cref="IStageProfile"/>
    /// and read by handlers via <see cref="IActiveStageProfile.Has(StageFeature)"/>.
    ///
    /// Adding a new flag here MUST also bump the matching <c>stage_{id}.txt</c>
    /// snapshot under <c>Tests/AiDriver/Stages/Snapshots/</c> — the snapshot
    /// tests serialize the full feature set per stage and assert against the
    /// golden file. This makes any "what fires when" change visible in code
    /// review.
    /// </summary>
    [Flags]
    public enum StageFeature
    {
        None                       = 0,

        OvertakeReward             = 1 << 0,
        GotPassedPenalty           = 1 << 1,
        MicroSectorPositionBonus   = 1 << 2,
        LapPositionBonus           = 1 << 3,
        CarHitCarPenalty           = 1 << 4,
        DraftBonus                 = 1 << 5,
        TireOverstressPenalty      = 1 << 6,
        FuelMarginPenalty          = 1 << 7,
        FuelOutTerminal            = 1 << 8,
        PunctureOffTrackTerminal   = 1 << 9,
        CleanDrivingBonus          = 1 << 10,
        HoldPositionBonus          = 1 << 11,

        OpponentObservations       = 1 << 12,
        TireObservations           = 1 << 13,
        FuelObservations           = 1 << 14,
        PersonalityObservations    = 1 << 15,
        FrontConeRayObservations   = 1 << 16,
    }

    /// <summary>Initial fuel-load sampling regime for the episode.</summary>
    public enum FuelSamplingMode
    {
        /// <summary>Flat 100 L — fuel is abundant, no lift-and-coast pressure.</summary>
        Abundant,
        /// <summary>Sampled [0.6, 1.4] laps × reference burn — forces fuel-margin discovery.</summary>
        Scarcity,
    }

    /// <summary>Per-episode personality vector sampling regime.</summary>
    public enum PersonalitySamplingMode
    {
        /// <summary>Uniform random across all six dimensions — broad exploration.</summary>
        Uniform,
        /// <summary>Pick one of six curated archetypes + ±noise — targeted distillation.</summary>
        Archetype,
    }

    /// <summary>
    /// Immutable bundle that captures "what is active at stage N" for one
    /// curriculum stage. Resolved per episode by <see cref="IActiveStageProfile"/>
    /// from <see cref="IStageIdProvider"/>. Every concrete profile is
    /// <c>internal sealed</c> and registered via <c>StageProfileSystemInstaller</c>.
    ///
    /// Modeled after <see cref="UnityPpoRacingTrainer.Core.AiDriver.Versions.IAiDriverVersionProfile"/>.
    /// </summary>
    public interface IStageProfile
    {
        int StageId { get; }
        string Name { get; }
        StageFeature Features { get; }

        /// <summary>Number of opponent agents expected to share the race in this
        /// stage. Drives whether <c>RaceStateService</c> publishes
        /// <c>OvertakeEvent</c> at all; zero means "warmup / solo" and
        /// suppresses event-bus traffic at the publisher.</summary>
        int ExpectedOpponentCount { get; }

        FuelSamplingMode Fuel { get; }
        PersonalitySamplingMode Personality { get; }
    }

    public static class StageProfileExtensions
    {
        public static bool Has(this IStageProfile p, StageFeature f) => (p.Features & f) == f;
    }
}
