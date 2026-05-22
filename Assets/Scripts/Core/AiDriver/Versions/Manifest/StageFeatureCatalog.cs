using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.AiDriver.Training.Stages;

namespace UnityPpoRacingTrainer.Core.AiDriver.Versions.Manifest
{
    /// <summary>
    /// Maps the snake_case keys used in manifest <c>stages[].features</c>
    /// arrays to <see cref="StageFeature"/> enum bits. Unknown keys throw —
    /// silent fallback to <see cref="StageFeature.None"/> would let a typo
    /// disable a reward channel without anyone noticing.
    ///
    /// Adding a new <see cref="StageFeature"/> bit MUST add the matching
    /// key here in the same commit, or every existing manifest that lists
    /// it stops parsing.
    /// </summary>
    public static class StageFeatureCatalog
    {
        public static readonly IReadOnlyDictionary<string, StageFeature> ByKey =
            new Dictionary<string, StageFeature>(StringComparer.OrdinalIgnoreCase)
            {
                ["overtake_reward"] = StageFeature.OvertakeReward,
                ["got_passed_penalty"] = StageFeature.GotPassedPenalty,
                ["micro_sector_position_bonus"] = StageFeature.MicroSectorPositionBonus,
                ["lap_position_bonus"] = StageFeature.LapPositionBonus,
                ["car_hit_car_penalty"] = StageFeature.CarHitCarPenalty,
                ["draft_bonus"] = StageFeature.DraftBonus,
                ["tire_overstress_penalty"] = StageFeature.TireOverstressPenalty,
                ["fuel_margin_penalty"] = StageFeature.FuelMarginPenalty,
                ["fuel_out_terminal"] = StageFeature.FuelOutTerminal,
                ["puncture_off_track_terminal"] = StageFeature.PunctureOffTrackTerminal,
                ["clean_driving_bonus"] = StageFeature.CleanDrivingBonus,
                ["hold_position_bonus"] = StageFeature.HoldPositionBonus,
                ["opponent_observations"] = StageFeature.OpponentObservations,
                ["tire_observations"] = StageFeature.TireObservations,
                ["fuel_observations"] = StageFeature.FuelObservations,
                ["personality_observations"] = StageFeature.PersonalityObservations,
                ["front_cone_ray_observations"] = StageFeature.FrontConeRayObservations,
            };

        public static StageFeature Parse(IEnumerable<string> tokens)
        {
            var result = StageFeature.None;
            if (tokens == null) return result;
            foreach (var token in tokens)
            {
                if (string.IsNullOrWhiteSpace(token)) continue;
                if (!ByKey.TryGetValue(token.Trim(), out var bit))
                    throw new ArgumentException($"unknown stage feature '{token}'");
                result |= bit;
            }
            return result;
        }

        public static FuelSamplingMode ParseFuel(string s) => (s ?? "").Trim().ToLowerInvariant() switch
        {
            "abundant" => FuelSamplingMode.Abundant,
            "scarcity" => FuelSamplingMode.Scarcity,
            _ => throw new ArgumentException($"unknown fuel_sampling '{s}'")
        };

        public static PersonalitySamplingMode ParsePersonality(string s) => (s ?? "").Trim().ToLowerInvariant() switch
        {
            "uniform" => PersonalitySamplingMode.Uniform,
            "archetype" => PersonalitySamplingMode.Archetype,
            _ => throw new ArgumentException($"unknown personality_sampling '{s}'")
        };
    }
}
