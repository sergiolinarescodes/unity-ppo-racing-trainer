# `settings.json` Reference

`settings.json` at the repo root exposes every tunable physics, tire, reward, episode, and track-geometry constant the trainer reads. Edit a value, restart the trainer, see the change. Schema source-of-truth: `Assets/Scripts/Core/AiDriver/Config/TrainingSettings.cs`.

## Editing

- **Browser**: http://localhost:8765/settings (auto-built form; atomic write; one-deep backup at `settings.json.bak`).
- **By hand**: any text editor. Missing fields fall back to the baked defaults declared on the C# `TrainingSettings` record.

## Sections

### `episode`
| Field | Type | Default | Notes |
|---|---|---|---|
| `maxStepsPerEpisode` | int | 12000 | PPO step cap per training episode. Lower = more episodes per wall-second. |
| `offTrackTimeoutSec` | float | 0.5 | Off-track grace before episode terminates. |

### `physics`
Per-car physics tunings. Values prefixed `*` below are multiplied by `trackGeometry.carPhysicsCellSize` at runtime; do not pre-multiply in JSON.

Key fields: `maxSpeed*`, `maxAccel*`, `maxBrake*`, `maxSteer`, `steerRate`, `lateralGripFactor`, `slipReleaseFactor`, `speedInducedUndersteerGain`, `tractionCircleGain`, `wallBounceDamping`, `wallStunSeconds`. Full list in `TrainingSettings.cs::PhysicsSettings`.

### `tirePhysics`
Wear coefficients per stress source.

| Field | Default | Stress source |
|---|---|---|
| `kLateralG` | 0.001512 | sustained cornering G |
| `kSlip` | 0.011347 | sideways slide / drift |
| `kBurnout` | 0.01296 | powerslide |
| `kBrake` | 0.006048 | brake heat |
| `kKerbStress` | 1.5 | kerb riding multiplier |
| `hardBrakeThreshold` | 0.9 | reference brake input for lock-up shaping doc |
| `hardBrakePeakMul` | 5.0 | peak wear multiplier at brake = 1 |
| `hardBrakeExponent` | 6.0 | curve sharpness for hard-brake ramp |
| `brakeBlockadeInputThreshold` | 0.99 | brake input level treated as "pinned" |
| `brakeBlockadeHoldSeconds` | 0.2 | held-pedal duration before lock penalty |
| `brakeBlockadePenaltyMul` | 5.0 | extra wear multiplier while pinned past hold |
| `punctureThreshold` | 0.97 | wear fraction below which punctures cannot roll |
| `punctureGThreshold` | 6.0 | min lateral G for puncture roll |
| `punctureBaseChancePerSec` | 0.0 | base puncture chance (disabled by default) |
| `puncturedGripFactor` | 0.10 | grip multiplier on a punctured tire |

### `rewardShaper`
~80 constants for shaping reward beyond the potential-based base. Sub-grouped:

- `overtakes` — overtake bonus, hold-per-sector escalation, got-passed penalty, grid grace, aggression sampling.
- `position` — passive position score (per-sector / per-lap), clean-driving bonus, sector-clean bonus, hold-position bonus, terminal clean-race bonus.
- `contact` — car-car collision penalties, rear-end offender multiplier + flat penalty, low-HP victim escalation, destroy-victim extra, rear-end cooldown + overtake grace, asymmetric victim multiplier, min-impact gate.
- `threat` — front-cone closing detection, avoidance bonus, stuck-in-threat penalty, patience window, tire-waiver scale, stuck-speed gate.
- `pack` — proximity bonus, clean-follow bonus + hold window, min-driver-count gate.
- `pace` — beat/match best-lap bonuses, per-step pace shaping, projection margin + arc-fraction floor, peak-pace multipliers (overall + per-sector EMA improvement).
- `draftAndConsumables` — draft per-second bonus, draft-pass one-shot + min strength + lookback, fuel/tire margin penalties, reference fuel burn, terminal fuel-out + puncture-off-track penalties.

**Warning**: bumping reward constants mid-training collapses the policy. Always start a fresh `--run-id` after changing rewards.

### `trackGeometry`
Mesh geometry constants. `cellSize` and `carPhysicsCellSize` are referenced inside Burst jobs as compile-time const; runtime overrides apply to non-Burst readers only. The loader warns at startup if `trackGeometry.cellSize` diverges from the const value.

### `observation` (frozen)
Read-only. Mutation is ignored by the C# loader and rejected by the dashboard with HTTP 400. To actually change observation shape, edit `Assets/Scripts/Core/AiDriver/Policy/RacingObservationLayout.cs` const fields, retrain from scratch, and re-bake the agent prefab's `BehaviorParameters`.

## Fallback Semantics

| Condition | Behavior |
|---|---|
| File missing | log info, use baked defaults |
| Malformed JSON | log error with line/col, use baked defaults — no throw |
| Partial fields | merge over baked defaults; absent fields stay at default |
| `_schemaVersion` mismatch | log warning, attempt load anyway |
