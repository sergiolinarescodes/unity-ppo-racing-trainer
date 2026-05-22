# Architecture

## Process Model

```
            ┌────────────────────────────────────────────┐
            │  Python supervisor (train_unattended.py)   │
            │  - activates tools/python/.venv            │
            │  - launches mlagents-learn                 │
            │  - on non-zero exit, sleeps + --resume     │
            │  - copies final ONNX to Resources/         │
            └─────────────────┬──────────────────────────┘
                              │ spawns
                              ▼
                  ┌─────────────────────────┐
                  │   mlagents-learn (PPO)  │
                  │   - PyTorch policy net  │
                  │   - LSTM 128, 256x2 MLP │
                  └────┬────────────────┬───┘
                       │ gRPC           │
                       ▼                ▼
              ┌─────────────────────────────────┐
              │ Unity headless env (x NumEnvs)  │
              │  AiDriverTrainer.exe / .app /   │
              │  .x86_64                        │
              │                                 │
              │  TrainerBootstrap               │
              │    └─ Reflex container          │
              │        ├─ TrainingSettings      │
              │        ├─ Track / Terrain       │
              │        ├─ AiDriver Physics      │
              │        ├─ Policy + RewardShaper │
              │        └─ Race coordination     │
              └─────────────────────────────────┘
```

## Reflex DI Graph (Trainer)

`TrainerBootstrap.RegisterInstallers` adds installers in dependency order. Each installer is an `ISystemInstaller` that binds interface -> implementation in the Reflex container.

Order (abridged):
1. `TrainingSettingsSystemInstaller` — FIRST. Every downstream service can inject `ITrainingSettingsService` in its ctor.
2. `GridSystemInstaller`, `TerrainSystemInstaller`
3. `GhostPresentationSystemInstaller`, `TrackSystemInstaller`, `RealisticTrackGenerationSystemInstaller`
4. `AiDriverLoopSystemInstaller`, `AiDriverPhysicsSystemInstaller`
5. `AiDriverVersionsSystemInstaller(activeVersion)` — registers the version profile (currently only `Latest`).
6. Side-system stack (tires, fuel, draft, car-collision, race-state, circuit-tire-profile, driver-physics-registry).
7. `AiDriverPolicySystemInstaller` — depends on `IAiDriverVersionProfile`.
8. `TrainingSystemInstaller` (if `AIDRIVER_TRAINING` define) — episode runner + reward shaper.

No `FindObjectOfType` or `GameObject.Find`. Services are plain C# classes; the only MonoBehaviour is `TrainerBootstrap` itself + a `TickRunner` Unidad spawns.

## Observation Layout (`Assets/Scripts/Core/AiDriver/Policy/RacingObservationLayout.cs`)

60 floats per frame, 3 frames stacked = 180-dim input. Layout:

| Block | Floats | Source |
|---|---|---|
| Ego state | 7 | speed, lateral offset, heading error, yaw rate, steer/throttle smoothed |
| Lookahead curvature | 10 | 5 anchors x (curvature, off-center) at 0/0.1/0.3/0.7/1.5s |
| Wall feelers | 7 | front-fan raycast occupancy |
| Surface flag | 1 | on-track / kerb / off-track |
| Other cars | 12 | nearest 2 ahead + 2 behind: bearing, distance, dV, headingDelta |
| Draft state | 1 | current draft gain |
| Tire wear | 1 | per-car effective grip |
| Fuel | 1 | remaining laps' worth |
| Personality | 8 | aggression dial + sampled traits |
| Opponent cone rays | 10 | 5 forward-cone rays x (occupancy, closing speed) |

## Reward Shaping (`Assets/Scripts/Core/AiDriver/Training/RewardShaper.cs`)

Base: potential-based forward progress along the loop centerline + lateral-offset penalty + steering smoothness.

Shaped layers (all tunable via `settings.json -> rewardShaper`):
- **Overtake / position** — pass bonus, escalating per-sector hold, full-lap-held cement, got-passed penalty, passive position score per sector/lap.
- **Clean driving** — bonus per second without contact, threat-window-aware.
- **Contact** — car-crash coefficient, rear-end-offender multiplier, low-HP victim extra penalty.
- **Threat avoidance** — front-cone closing-speed shaping, stuck-in-threat penalty, tire-waiver scaling.
- **Pack racing** — proximity bonus, clean-follow bonus.
- **Pace** — beat/match best-lap bonuses (per-circuit, merge-min database in `tools/circuit_records/records.json`).

## Track Generation

Two generators live behind `IProceduralLoopGenerator`:

- `ShapeBasedLoopGenerator` — recipe-based, closed-by-construction rectangles + simple ovals (used by some scenarios).
- `RealisticTrackGenerator` — Burst-driven parallel search across the full shape catalog. Each step expands all `(shape, anchor port)` candidates in parallel via `ExpandFrontierJob`, scores by length-budget + turn-density + closure-proximity, and commits the best move. Closure tiers: managed-catalog snap -> Tier-2 BFS -> one-off cubic Bezier custom piece.

`CurriculumGeneratorSelector` routes between them. Authored-closure circuits in `circuits/stage_authored_closure/` provide hand-shaped exemplars the trainer can also pull from.

## Telemetry

- Per-race JSON dumps: `results/_telemetry/races/` — reservoir-sampled at 1-per-1000-episodes per env; capped at 50 newest files (C# sink).
- Per-circuit best laps: `tools/circuit_records/records.json` — merge-min semantics, survives every results/ wipe.
- TensorBoard event files: `results/<RunId>/`.

The Python dashboard (`tools/dashboard/server.py`) reads all three and serves visualizations on `:8765`.
