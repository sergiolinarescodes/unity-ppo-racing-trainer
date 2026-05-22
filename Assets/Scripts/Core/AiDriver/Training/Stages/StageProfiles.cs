namespace UnityPpoRacingTrainer.Core.AiDriver.Training.Stages
{
    /// <summary>
    /// Stage 0 — solo warmup. 200 agents per env share a RaceState but no
    /// opponent-aware shaping fires (overtake / sector / lap / car-hit /
    /// draft / clean / hold all OFF). Pure driving curriculum on abundant
    /// fuel + uniform personality. <see cref="ExpectedOpponentCount"/> is 0
    /// which suppresses <c>RaceStateService</c> publishing of
    /// <c>OvertakeEvent</c> at the source (closes the historical leak that
    /// caused phantom overtakes_lost telemetry when warmup had no opponents).
    /// </summary>
    internal sealed class Stage0SoloWarmupProfile : IStageProfile
    {
        public int StageId => 0;
        public string Name => "Stage0SoloWarmup";
        // Tire shaping active from stage 0: observations + overstress
        // penalty in warmup so the agent learns tire management from the
        // start. The wear sim already ticks at every stage; gating only
        // the signal/reward was the artifact.
        public StageFeature Features =>
            StageFeature.TireOverstressPenalty |
            StageFeature.TireObservations;
        public int ExpectedOpponentCount => 0;
        public FuelSamplingMode Fuel => FuelSamplingMode.Abundant;
        public PersonalitySamplingMode Personality => PersonalitySamplingMode.Uniform;
    }

    /// <summary>
    /// Stage 1 — 12-car grid. All multi-agent shaping unlocks (overtake,
    /// got-passed, sector + lap position bonuses, car-car contact penalty,
    /// draft, clean driving, hold position) plus opponent observations.
    /// Fuel still abundant; personalities still uniform-random.
    /// </summary>
    internal sealed class Stage1GridProfile : IStageProfile
    {
        public int StageId => 1;
        public string Name => "Stage1Grid";
        public StageFeature Features =>
            StageFeature.OvertakeReward |
            StageFeature.GotPassedPenalty |
            StageFeature.MicroSectorPositionBonus |
            StageFeature.LapPositionBonus |
            StageFeature.CarHitCarPenalty |
            StageFeature.DraftBonus |
            StageFeature.CleanDrivingBonus |
            StageFeature.HoldPositionBonus |
            StageFeature.OpponentObservations |
            // Tire shaping active at every stage.
            StageFeature.TireOverstressPenalty |
            StageFeature.TireObservations;
        public int ExpectedOpponentCount => 11;
        public FuelSamplingMode Fuel => FuelSamplingMode.Abundant;
        public PersonalitySamplingMode Personality => PersonalitySamplingMode.Uniform;
    }

    /// <summary>
    /// Stage 2 — fuel scarcity unlocks. Adds fuel-margin penalty + fuel-out
    /// terminal + fuel observations on top of Stage 1's grid shaping. Fuel
    /// sampler flips to scarcity mode (0.6–1.4 lap-margin sampler).
    /// </summary>
    internal sealed class Stage2FuelScarcityProfile : IStageProfile
    {
        public int StageId => 2;
        public string Name => "Stage2FuelScarcity";
        public StageFeature Features =>
            StageFeature.OvertakeReward |
            StageFeature.GotPassedPenalty |
            StageFeature.MicroSectorPositionBonus |
            StageFeature.LapPositionBonus |
            StageFeature.CarHitCarPenalty |
            StageFeature.DraftBonus |
            StageFeature.CleanDrivingBonus |
            StageFeature.HoldPositionBonus |
            StageFeature.FuelMarginPenalty |
            StageFeature.FuelOutTerminal |
            StageFeature.OpponentObservations |
            StageFeature.FuelObservations |
            // Tire shaping active at every stage.
            StageFeature.TireOverstressPenalty |
            StageFeature.TireObservations;
        public int ExpectedOpponentCount => 11;
        public FuelSamplingMode Fuel => FuelSamplingMode.Scarcity;
        public PersonalitySamplingMode Personality => PersonalitySamplingMode.Uniform;
    }

    /// <summary>
    /// Stage 3 — tire stress + puncture risk. Adds tire-overstress penalty +
    /// puncture-off-track terminal + tire observations on top of Stage 2.
    /// </summary>
    internal sealed class Stage3TireFuelProfile : IStageProfile
    {
        public int StageId => 3;
        public string Name => "Stage3TireFuel";
        public StageFeature Features =>
            StageFeature.OvertakeReward |
            StageFeature.GotPassedPenalty |
            StageFeature.MicroSectorPositionBonus |
            StageFeature.LapPositionBonus |
            StageFeature.CarHitCarPenalty |
            StageFeature.DraftBonus |
            StageFeature.CleanDrivingBonus |
            StageFeature.HoldPositionBonus |
            StageFeature.FuelMarginPenalty |
            StageFeature.FuelOutTerminal |
            StageFeature.TireOverstressPenalty |
            StageFeature.PunctureOffTrackTerminal |
            StageFeature.OpponentObservations |
            StageFeature.FuelObservations |
            StageFeature.TireObservations;
        public int ExpectedOpponentCount => 11;
        public FuelSamplingMode Fuel => FuelSamplingMode.Scarcity;
        public PersonalitySamplingMode Personality => PersonalitySamplingMode.Uniform;
    }

    /// <summary>
    /// Stage 4 — authored circuits + personality archetypes. Adds personality
    /// observations and flips personality sampler to archetype mode. Geometry
    /// switches to the authored-card library (see <c>CurriculumStages</c>
    /// <c>AuthoredStageId = 4</c>) — orthogonal to feature flags.
    /// </summary>
    internal sealed class Stage4AuthoredTwoCarProfile : IStageProfile
    {
        public int StageId => 4;
        public string Name => "Stage4AuthoredTwoCar";
        public StageFeature Features =>
            StageFeature.OvertakeReward |
            StageFeature.GotPassedPenalty |
            StageFeature.MicroSectorPositionBonus |
            StageFeature.LapPositionBonus |
            StageFeature.CarHitCarPenalty |
            StageFeature.DraftBonus |
            StageFeature.CleanDrivingBonus |
            StageFeature.HoldPositionBonus |
            StageFeature.FuelMarginPenalty |
            StageFeature.FuelOutTerminal |
            StageFeature.TireOverstressPenalty |
            StageFeature.PunctureOffTrackTerminal |
            StageFeature.OpponentObservations |
            StageFeature.FuelObservations |
            StageFeature.TireObservations |
            StageFeature.PersonalityObservations;
        public int ExpectedOpponentCount => 11;
        public FuelSamplingMode Fuel => FuelSamplingMode.Scarcity;
        public PersonalitySamplingMode Personality => PersonalitySamplingMode.Archetype;
    }

    /// <summary>
    /// Stage 5 — every shaping channel + observation on. Notably DROPS the
    /// PunctureOffTrackTerminal: tire wear continues to degrade grip per
    /// WearGripScale, but a worn tire off-track no longer terminates the
    /// episode. Performance loss alone drives the lap-time gradient.
    /// </summary>
    internal sealed class Stage5PackSelfPlayProfile : IStageProfile
    {
        public int StageId => 5;
        public string Name => "Stage5PackSelfPlay";
        public StageFeature Features =>
            StageFeature.OvertakeReward |
            StageFeature.GotPassedPenalty |
            StageFeature.MicroSectorPositionBonus |
            StageFeature.LapPositionBonus |
            StageFeature.CarHitCarPenalty |
            StageFeature.DraftBonus |
            StageFeature.CleanDrivingBonus |
            StageFeature.HoldPositionBonus |
            StageFeature.FuelMarginPenalty |
            StageFeature.FuelOutTerminal |
            StageFeature.TireOverstressPenalty |
            StageFeature.OpponentObservations |
            StageFeature.FuelObservations |
            StageFeature.TireObservations |
            StageFeature.PersonalityObservations |
            StageFeature.FrontConeRayObservations;
        public int ExpectedOpponentCount => 11;
        public FuelSamplingMode Fuel => FuelSamplingMode.Scarcity;
        public PersonalitySamplingMode Personality => PersonalitySamplingMode.Archetype;
    }
}
