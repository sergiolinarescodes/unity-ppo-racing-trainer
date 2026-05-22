# Telemetry Schema

Three on-disk surfaces consumed by external tooling (the Python dashboard, future analyses, you). All three are stable: field renames break the dashboard and any community scripts. Add fields freely; rename or repurpose with a major-version bump.

## 1. `tools/circuit_records/records.json` — persistent best-lap database

The only file outside `results/` that survives a results-wipe. Written by both the C# trainer (when a new flying lap beats the stored best) and the Python tierlist server (when the aggregator processes flying laps). Atomic writes (tmp + replace), convergent under min-merge.

```json
{
  "version": 1,
  "circuits": {
    "stage_authored_closure/circuit_0001": {
      "best_lap_seconds": 23.471,
      "run_id": "my-run-1",
      "timestamp_utc": "2026-05-22T18:14:09.0123456Z",
      "writer": "csharp"
    },
    "stage_authored_closure/circuit_0004": {
      "best_lap_seconds": 25.108
    }
  }
}
```

Field meanings:

| Field                   | Type     | Always present? | Notes                                               |
|-------------------------|----------|-----------------|-----------------------------------------------------|
| `version`               | int      | yes             | bump on schema break                                |
| `circuits`              | object   | yes             | keyed by circuit id (path-style)                    |
| `*.best_lap_seconds`    | float    | yes             | seconds, ≥ 0; merge-min update                      |
| `*.run_id`              | string   | only on writer  | last writer's `--run-id`; empty when ad-hoc         |
| `*.timestamp_utc`       | ISO 8601 | only on writer  | UTC, `o`-format                                     |
| `*.writer`              | string   | only on writer  | `"csharp"` or `"python"`                            |

Reader contract: ignore unknown fields, treat missing optional fields as not-present. The C# reader is a hand-rolled JSON tokeniser (`CircuitRecordsStore.ReadFromDisk`) that only consumes `best_lap_seconds` — it tolerates any other field you add inside each circuit object.

## 2. `results/<run_id>/race_telemetry/*.json` — per-race race records

One file per race. Schema is `RaceRecordDto` (see `Assets/Scripts/Core/AiDriver/Telemetry/RaceTelemetryDtos.cs`); the C# side serialises it with `JsonUtility`, so field names are *exact* snake_case strings — DO NOT rename them.

```json
{
  "race_id": "race-2026-05-22T18-14-09-pid-12345-ep-42",
  "captured_at_utc": "2026-05-22T18:14:09Z",
  "env_pid": 12345,
  "episode_index": 42,
  "stage_id": 4,
  "sample_hz": 5,
  "circuit": { "id": "stage_authored_closure/circuit_0001", "length_m": 312.4, "piece_count": 28 },
  "duration_s": 71.2,
  "drivers": [
    {
      "car_id": 0,
      "display_name": "agent-0",
      "personality": [0.71, 0.42, 0.83, 0.12, 0.55, 0.61, 0.0, 0.0],
      "end_state": {
        "reason": "AllLapsCompleted",
        "final_position": 1,
        "finish_position": 1,
        "laps_completed": 3,
        "cumulative_reward": 47.21,
        "fuel_l_final": 12.3,
        "fuel_l_start": 35.0,
        "tire_l_final": 0.62,
        "tire_r_final": 0.59,
        "punctured_l": false,
        "punctured_r": false,
        "wall_hits": 0,
        "car_hits": 1,
        "overtakes_made": 2,
        "overtakes_lost": 0,
        "total_race_time_s": 71.2
      },
      "samples": [ /* DriverSampleDto[] @ sample_hz */ ],
      "lap_times_s": [23.5, 23.4, 24.3]
    }
  ],
  "events": [ /* RaceEventDto[]: lap_complete | overtake | puncture | car_hit | wall_hit | off_track | back_on_track | fuel_out | draft_change */ ],
  "end_reason": "AllDriversResolved",
  "lap_target": 3,
  "finishers_count": 4,
  "eliminated_count": 0
}
```

Key invariants:

- `sample_hz` is the rate of `drivers[].samples[]` — 5 Hz by default, configurable via `settings.json`.
- `drivers[].personality` is exactly **8 floats** — 6 active dimensions + 2 reserved (zero today). Same vector the policy was conditioned on for that race.
- `events[].type` is a closed enum; the discriminator string is documented inline in `RaceTelemetryDtos.cs:RaceEventDto`.
- Race-scoped fields (`end_reason`, `lap_target`, `finishers_count`, `eliminated_count`) stay zero/empty in legacy mode so old race JSON round-trips through the same DTO type.
- Writes are atomic via `DiskJsonRaceSink` — never partial.

## 3. `results/<run_id>/training/...` — ML-Agents native output

Untouched by this project. Read it with TensorBoard. Schema is whatever ML-Agents 4.0.3 writes; treat it as opaque and use `tensorboard --logdir results --port 6006` to consume.

## Stability promise

- **`circuit_records/records.json`** — fields above are stable to v1. New keys can be added under each circuit object without a version bump.
- **`race_telemetry/*.json`** — fields above are stable. Anything added since v0.2.0 (see `CHANGELOG.md`) is additive; older readers parse newer files by ignoring unknown fields.
- The training subdir is owned by ML-Agents; we make no promise about it.

If you need a new field, prefer adding it to one of the existing DTOs rather than introducing a parallel file. The dashboard already knows where to look.
