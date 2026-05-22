using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.AiDriver.Telemetry
{
    // ----- Pure data classes, mutable, [Serializable] so Unity's built-in
    // JsonUtility round-trips them. Field names are snake_case because that
    // is the on-disk schema the Python dashboard and any future external
    // tooling consumes. The schema is therefore the single source of truth
    // for the train-time dashboard AND any in-game UI that surfaces a race
    // summary — no parallel format exists. Keep field names stable.

    /// <summary>Per-race metadata captured at <c>BeginRace</c>.</summary>
    public readonly struct RaceContext
    {
        public readonly long EpisodeIndex;
        public readonly int StageId;
        public readonly string CircuitId;
        public readonly float CircuitLengthM;
        public readonly int CircuitPieceCount;
        public readonly int EnvPid;

        public RaceContext(long episodeIndex, int stageId, string circuitId,
            float circuitLengthM, int circuitPieceCount, int envPid)
        {
            EpisodeIndex = episodeIndex;
            StageId = stageId;
            CircuitId = circuitId ?? string.Empty;
            CircuitLengthM = circuitLengthM;
            CircuitPieceCount = circuitPieceCount;
            EnvPid = envPid;
        }
    }

    [Serializable]
    public sealed class CircuitInfoDto
    {
        public string id;
        public float length_m;
        public int piece_count;
    }

    [Serializable]
    public sealed class DriverSampleDto
    {
        public float t;
        public int lap;
        public float lap_frac;
        public int sector;        // 0..2 derived from lap_frac (lap_frac * 3, floored, capped at 2)
        public float speed;
        public float heading;     // radians, 0 = +Z, CCW positive (matches CarPhysicsTickedEvent)
        public float fuel_l;
        public float tire_l;
        public float tire_r;
        public float draft;
        public float reward_cum;
        public Vector3 pos;
    }

    [Serializable]
    public sealed class EndStateCardDto
    {
        public string reason;
        public int final_position;
        public int laps_completed;
        public float cumulative_reward;
        public float fuel_l_final;
        public float fuel_l_start;
        public float tire_l_final;
        public float tire_r_final;
        public bool punctured_l;
        public bool punctured_r;
        public int wall_hits;
        public int car_hits;
        public int overtakes_made;
        public int overtakes_lost;
        // Race-scoped fields. Stay zero/empty in legacy mode so old race JSON
        // round-trips unchanged through the same DTO type. finish_position is
        // 1..N for drivers who completed the lap target this race, 0 for
        // eliminated. total_race_time_s is wall-clock-from-spawn-to-finish
        // for finishers, 0 otherwise.
        public int finish_position;
        public float total_race_time_s;
    }

    /// <summary>Per-driver record. <c>personality</c> is the 8-float
    /// personality conditioning vector the policy saw this race.</summary>
    [Serializable]
    public sealed class DriverRaceRecordDto
    {
        public int car_id;
        public string display_name;
        public float[] personality;            // 8 floats
        public EndStateCardDto end_state;
        public List<DriverSampleDto> samples;
        public List<float> lap_times_s;
    }

    /// <summary>Generic race event. Optional fields populated per type. The
    /// <c>type</c> discriminator is documented in the plan:
    /// <c>lap_complete | overtake | puncture | car_hit | wall_hit |
    /// off_track | back_on_track | fuel_out | draft_change</c>.</summary>
    [Serializable]
    public sealed class RaceEventDto
    {
        public float t;
        public string type;
        public int car_id;
        public int lap;
        public float lap_time;
        public int passer;
        public int passed;
        public int new_position;
        public string side;
        public float wear_at_puncture;
        public int a;
        public int b;
        public float impact_speed;
        public float strength;
        public int leader_id;
    }

    [Serializable]
    public sealed class RaceRecordDto
    {
        public string race_id;
        public string captured_at_utc;
        public int env_pid;
        public long episode_index;
        public int stage_id;
        public int sample_hz;     // sampling rate of DriverRaceRecordDto.samples (5 by default)
        public CircuitInfoDto circuit;
        public float duration_s;
        public List<DriverRaceRecordDto> drivers;
        public List<RaceEventDto> events;
        // Race-scoped fields. end_reason ∈ { "in_progress", "AllDriversResolved",
        // "MaxStepsCap", "Aborted", "" /* legacy */ }. lap_target is the
        // per-race lap count when race-scoped; 0 in legacy. finishers_count
        // + eliminated_count both stay 0 in legacy. Existing readers ignore
        // unknown fields; older JSON parses unchanged because every field
        // defaults to zero/empty.
        public string end_reason;
        public int lap_target;
        public int finishers_count;
        public int eliminated_count;
    }

    /// <summary>Header-only record used by the list / index views.</summary>
    [Serializable]
    public sealed class RaceSummaryDto
    {
        public string race_id;
        public string captured_at_utc;
        public int env_pid;
        public long episode_index;
        public int stage_id;
        public string circuit_id;
        public float duration_s;
        public int driver_count;

        public static RaceSummaryDto From(RaceRecordDto r)
        {
            return new RaceSummaryDto
            {
                race_id = r.race_id,
                captured_at_utc = r.captured_at_utc,
                env_pid = r.env_pid,
                episode_index = r.episode_index,
                stage_id = r.stage_id,
                circuit_id = r.circuit != null ? r.circuit.id : string.Empty,
                duration_s = r.duration_s,
                driver_count = r.drivers != null ? r.drivers.Count : 0,
            };
        }
    }
}
