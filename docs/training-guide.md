# Training Guide

## One-time setup

1. **Clone with the Unidad submodule**:
   ```bash
   git clone --recurse-submodules https://github.com/sergiolinarescodes/unity-ppo-racing-trainer.git
   cd unity-ppo-racing-trainer
   ```

2. **Python venv** (Python 3.10, torch 2.2.2, mlagents 1.1.0):
   ```bash
   python tools/setup.py
   # Optional NVIDIA GPU build:
   python tools/setup.py --cuda
   ```

3. **Open in Unity 6000.4.0f1**. First import takes 3–5 minutes (Burst AOT, shader caches).

4. **Build the headless trainer**: Unity menu `Build > AI Driver Trainer (Headless)`. Output:
   - Windows: `Build/AiDriverTrainer/AiDriverTrainer.exe`
   - macOS: `Build/AiDriverTrainer/AiDriverTrainer.app/Contents/MacOS/AiDriverTrainer`
   - Linux: `Build/AiDriverTrainer/AiDriverTrainer.x86_64`

## Run training (canonical)

```bash
python tools/train_unattended.py
```

This is the no-arg form. Defaults:
- `--run-id` — auto-timestamped (`v1-YYYYMMDD-HHmm`)
- `--num-envs` — 4 (parallel Unity processes)
- `--max-hours` — 0 (no wall-clock limit; trainer runs until `max_steps`)
- `--config-path` — `Assets/_Bootstrap/Configs/MlAgents/racing_driver.yaml`
- `--base-port` — 5104
- `--timeout-wait` — 300 seconds (covers cold-start Burst AOT)
- `--race-scoped` — `0` (per-car terminals, not race-scoped 3-lap episodes)

Power-user overrides:
```bash
python tools/train_unattended.py --num-envs 8 --max-hours 12
python tools/train_unattended.py --run-id my-experiment-1
python tools/train_unattended.py --init-from v1-20260520-1430  # fine-tune from existing ONNX
```

Press Ctrl+C to stop cleanly. The supervisor copies the final ONNX into `Assets/Resources/AiDriver/Policies/` (named `RacingDriver.onnx`).

## Watch progress

```bash
# Reward curve + losses
tensorboard --logdir results --port 6006

# Dashboard (separate terminal): live stats, race telemetry, settings editor
python tools/dashboard/server.py
# -> http://localhost:8765/training
# -> http://localhost:8765/races
# -> http://localhost:8765/settings
```

## Edit settings without restarting

`settings.json` at the repo root holds every tunable physics, reward, episode, tire, and track-geometry constant. Edit a value -> restart the trainer (Ctrl+C, relaunch) -> see the change. The running supervisor uses its already-loaded values; live hot-reload isn't wired yet.

Edit options:
- Browser at http://localhost:8765/settings (atomic write, one-deep backup at `settings.json.bak`, frozen fields disabled).
- Any text editor.

Field reference: [`settings.md`](settings.md).

## Run inference on a trained ONNX

1. Drop the `.onnx` into `Assets/Resources/AiDriver/Policies/`.
2. Open `Assets/_Bootstrap/TrainerTest.unity` and press Play. The bootstrap loads the most recent ONNX in `Policies/`. If the folder is empty, the agent runs a random policy.

## Editor-attached training (visible, single env)

```bash
python tools/train_editor.py
```

Then press Play in the Unity Editor. Slower than headless (single env, render pipeline active) but you can watch the policy improve in real time and inspect gizmos. Useful for reward shaping debugging.

## Recovery after crash

The supervisor wraps `mlagents-learn` in a restart loop:
- Non-zero exit -> wait 30s -> relaunch with `--resume`.
- Wall-clock timeout (`--max-hours`) -> kill, copy best ONNX, exit cleanly.
- Orphan Unity headless / mlagents-learn processes -> killed via psutil at every transition.

If the headless trainer itself OOMs/crashes, the supervisor's outer loop relaunches and mlagents-learn picks up from the last checkpoint.

## What to watch in the first hour

- **Reward curve at ~50k steps**: should trend up. If flat, reward shaping is broken — abort and re-tune before burning hours.
- **`circuit_records/records.json`**: per-circuit best laps appear as agents complete clean flying laps. An empty file after 100k steps means every lap is a wreck.
- **Telemetry `races/`**: reservoir-sampled per-race JSON dumps. The dashboard's `/races` page renders these — useful for visual debugging of corner-entry behavior.
