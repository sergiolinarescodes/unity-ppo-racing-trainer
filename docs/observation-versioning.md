# Observation Versioning

The trained ONNX policy is shape-locked to the observation vector it was trained on. Changing the float count or the per-index meaning silently breaks every previously-trained model. This document is the contract for editing `RacingObservationLayout`.

## The shape

- **60 floats per frame** — `RacingObservationLayout.FloatsPerFrame`
- **Stacked 3×** by ML-Agents → **180-dim policy input**

Split (see top of `Assets/Scripts/Core/AiDriver/Policy/RacingObservationLayout.cs`):

| Range  | Block        | What                                                       |
|--------|--------------|------------------------------------------------------------|
| [0..6] | Ego          | velocity / yaw / lateral offset / heading error / inputs   |
| [7..16]| Lookahead    | 5 anchors × (curvature, half-width)                        |
| [17..23]| Wall feelers | 7 rays at body-relative angles                            |
| [24]   | Surface flag | 0 asphalt / 0.5 kerb / 1 off-track                         |
| [25..36]| Other cars   | 2 ahead + 2 behind × (dist, bearing, speed)               |
| [37]   | Draft        | smoothed strength [0, 1]                                   |
| [38..40]| Tires       | wear L, wear R, puncture flag                              |
| [41]   | Fuel         | laps remaining [0, 1]                                      |
| [42..49]| Personality | 6 active + 2 reserved                                      |
| [50..54]| Front cone  | 5 rays — opponent occupancy                                |
| [55..59]| Front cone  | 5 rays — closing speed (signed)                            |

## The rule

**If you change the observation, freeze the old one first.**

The repo's pattern (see [`docs/snapshot-version.md`](snapshot-version.md)) is:

1. Take the current `Versions/Latest/` and copy it to `Versions/V<N>/`. That snapshot keeps the old layout + the old ONNX-compatible inference path so historical models still load.
2. Edit `Versions/Latest/RacingObservationLayout` (and the matching `EgoObservationLayout`, opponent ray writer, etc.) to the new shape.
3. Retrain from scratch. There is no migration path — a 60-float ONNX cannot consume a 61-float observation.

## What counts as "changing the observation"

| Change                                              | Breaks ONNX? | Action                            |
|-----------------------------------------------------|--------------|-----------------------------------|
| Add a new float                                     | yes          | snapshot + retrain                |
| Remove a float                                      | yes          | snapshot + retrain                |
| Reorder existing floats                             | yes          | snapshot + retrain                |
| Change the **meaning** of a float (e.g. scale)      | usually yes  | retrain; snapshot if observable   |
| Change a clamp range without changing magnitude     | no           | safe                              |
| Rename a constant in C#                             | no           | safe                              |
| Add a comment / refactor extraction logic           | no           | safe                              |

When in doubt, run inference with the existing ONNX on a known-good seed/stage *before* you change anything and after. If the policy still drives sanely, the change was safe.

## How to add a new block (the long-form)

Don't graft a single float onto the end. Group it with related signals:

1. Identify the conceptual block (e.g., "weather" — 3 floats: rain intensity, grip multiplier, visibility).
2. Bump `FloatsPerFrame` and add a new block-size constant alongside `BaseObservationFloats` / `RaceContextFloats` / `FrontConeFloats` (e.g., `WeatherFloats = 3`).
3. Update the layout table at the top of `RacingObservationLayout.cs` — this is the authoritative contract.
4. Write the new range in `WriteFrame` *after* the existing blocks (preserve indices for the old blocks).
5. Add a stage-feature flag in `StageFeature` (`WeatherObservations`) and gate the writer so stages without weather still get zeros at those indices.
6. Snapshot the previous canonical, then retrain.

## Runtime checks

`RacingObservationLayout.FloatsPerFrame` is read by `AiDriverAgentBehaviour` to declare the VectorObservation size to ML-Agents. If the constant and the writer disagree, ML-Agents throws on the *first* observation. There is no silent corruption — but there's also no compile-time check that the writer fills exactly `FloatsPerFrame` slots, so a discipline of "writer-count must equal the constant" is on the author.

A planned addition: a snapshot test that hashes the observation vector for a fixed `(seed, stage)` and locks the hash in source. Until that exists, treat the layout table at the top of `RacingObservationLayout.cs` as the source of truth and review any PR that touches it as if it were a wire-protocol change.

## ML-Agents YAML

`Assets/_Bootstrap/Configs/MlAgents/*.yaml` declares the observation size implicitly via the network — usually it auto-detects, but `time_horizon`, `batch_size`, and `buffer_size` are tuned for the current `FloatsPerFrame × StackSize`. After a layout change, the YAML defaults should still hold for one or two reasonable values, but a 2× change in input size warrants re-tuning `batch_size`.
