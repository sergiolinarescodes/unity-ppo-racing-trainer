# Unity PPO Racing Trainer

PPO neural-network car racing agents in Unity. Multi-agent self-play across an authored circuit library, tire degradation, draft physics, race-scoped 3-lap episodes, and a Python dashboard for live training stats, race telemetry, and in-browser settings editing.

![Agent learning to corner](readme.gif)

---

## Demo

<!-- [3 GIFs: random policy / mid-training corner attack / fully-trained pack race — placeholders] -->

*GIFs will be added showing training progression.*

---

## Stack

- **Unity 6 LTS** (6000.4.0f1) — headless build target
- **ML-Agents 4.0.3** (Unity package) + **mlagents 1.1.0** (Python)
- **PyTorch 2.2.2** (CPU default, optional CUDA cu121)
- **Reflex DI** — pure-C# constructor injection
- **Unidad** — services, event bus, ticking, modifiers, registries ([sergiolinarescodes/unidad](https://github.com/sergiolinarescodes/unidad))
- **Unity Burst + Jobs** — parallel track-shape search
- **PPO** with LSTM memory + 256x2 policy/value head
- **Newtonsoft.Json** — runtime `settings.json` loader

---

## Prerequisites (Windows, macOS, Linux)

| OS      | Install Python 3.10                              | Install Unity 6 LTS                        |
|---------|--------------------------------------------------|--------------------------------------------|
| Windows | `winget install Python.Python.3.10`              | Unity Hub -> install `6000.4.0f1`          |
| macOS   | `brew install python@3.10`                       | Unity Hub for macOS -> install `6000.4.0f1`|
| Linux   | `sudo apt install python3.10 python3.10-venv`    | Unity Hub for Linux -> install `6000.4.0f1`|

Git with submodule support is also required. Tooling is pure Python — no PowerShell, no bash dependencies.

---

## Quick Start

```bash
# 1. Clone with the Unidad submodule
git clone --recurse-submodules https://github.com/sergiolinarescodes/unity-ppo-racing-trainer.git
cd unity-ppo-racing-trainer

# 2. One-time Python venv (~5 min). Add --cuda for an NVIDIA GPU torch build.
python tools/setup.py

# 3. Open the project in Unity 6000.4.0f1 — first import takes 3-5 min.

# 4. Build the headless trainer: Unity menu Build > AI Driver Trainer (Headless)
#    Produces a per-OS binary under Build/AiDriverTrainer/.

# 5. (optional) Tweak car physics, reward shaping, tire wear, or track geometry
#    by editing settings.json at the repo root before you start training.
#    Every field is optional — leave one out and it falls back to a baked
#    default. Same file can be edited live in the browser from /settings
#    (see step 7).

# 6. Train. No arguments needed for a default run:
python tools/train_unattended.py

# Optional flags:
python tools/train_unattended.py --num-envs 8          # more parallel envs
python tools/train_unattended.py --max-hours 12        # wall-clock budget
python tools/train_unattended.py --run-id my-run-1     # custom run name
python tools/train_unattended.py --init-from my-run-1  # fine-tune from a prior run

# 7. (separate terminal) Watch progress and edit settings live:
tensorboard --logdir results --port 6006
python tools/dashboard/server.py   # http://localhost:8765/{training,races,authored,settings}
```

To run a trained policy in the Editor: open `Assets/_Bootstrap/TrainerTest.unity` and press Play. The bootstrap loads the most recent `.onnx` from `Assets/Resources/AiDriver/Policies/`. If the folder is empty, the agent runs a random policy.

Press Ctrl+C to stop training cleanly — the supervisor saves the trained policy and shuts the Unity envs down for you.

---

## Architecture

Installer-driven dependency injection. `TrainerBootstrap` registers each subsystem; Reflex wires constructor dependencies. No `FindObjectOfType`, no `MonoBehaviour.Update()`-based scripts. Every constant the trainer reads is loaded from `settings.json` at startup.

### Subsystems

- **Track Generation** — Burst jobs expand track-shape candidates in parallel; cubic Bezier closure handles residual gaps. Authored circuits live in `circuits/stage_authored_closure/`.
- **Physics** — Kinematic car model: traction circle, lateral-G understeer, kerb grip, wall stun, draft, tire wear, fuel.
- **Policy** — 60-float observation × 3 stacked frames = 180-dim input, 2 continuous actions (steer, throttle/brake).
- **Training Loop** — Episode runner + reward shaper (personality-aware shaping for overtake / threat / pace / pack). Every shaping coefficient lives in `settings.json`.
- **Race Coordination** — 1 race = 1 PPO episode per driver. Episode ends when every driver finishes the lap target or is eliminated.

### Observation (180-dim)

Ego state (7) + lookahead curvature (10) + wall feelers (7) + surface flag + nearest-car bearings (12) + draft + tire wear + fuel + personality (8) + opponent cone rays (10), clamped roughly to [-1, 1] and stacked 3x.

### Reward Shaping

Base reward: progress along the centerline, with penalties for lateral offset and jerky steering. On top of that: overtake bonus + escalating per-sector hold credit + lump bonus for holding a pass for a full lap, got-passed penalty, off-track timeout, wall hits, tire puncture, and per-circuit pace targets pulled from a persistent best-lap database. Around 80 tunable coefficients, all in `settings.json`.

---

## `settings.json`

Every physics, tire, reward, episode, and track-geometry constant the trainer reads is exposed in `settings.json` at the repo root. Edit a value → restart the trainer → see the change. Missing fields fall back to baked defaults. Observation-layout fields are read-only (they're baked into the trained policy file).

Two ways to edit:
- **Browser**: http://localhost:8765/settings while the dashboard is running. Atomic writes with a one-deep backup at `settings.json.bak`. Frozen fields are disabled in the UI.
- **By hand**: any text editor. The schema mirrors `Assets/Scripts/Core/AiDriver/Config/TrainingSettings.cs`.

Full field reference: [`docs/settings.md`](docs/settings.md).

---

## Repo Layout

```
unity-ppo-racing-trainer/
├── README.md / LICENSE / .gitignore
├── settings.json                       <- runtime-tunable parameters
├── docs/                               <- settings reference, architecture, training guide
├── Assets/
│   ├── _Bootstrap/
│   │   ├── Trainer.unity               <- training scene
│   │   ├── TrainerTest.unity           <- in-Editor inference scene
│   │   └── Configs/MlAgents/           <- PPO training configs (yaml)
│   ├── Resources/AiDriver/             <- agent prefab + driver profiles
│   │   └── Policies/                   <- trained .onnx files land here
│   └── Scripts/Core/                   <- AiDriver, Track, Terrain, Ghost, Bootstrap
├── Packages/
│   ├── manifest.json
│   └── com.unidad.core/                <- git submodule -> sergiolinarescodes/unidad
├── circuits/stage_authored_closure/    <- authored circuit library
├── tools/
│   ├── setup.py                        <- one-time Python venv + dependency install
│   ├── train_unattended.py             <- headless training supervisor
│   ├── train_editor.py                 <- Editor-attached training (visible, single env)
│   ├── requirements.txt                <- pinned Python deps
│   ├── dashboard/server.py             <- http://localhost:8765/
│   └── circuit_records/records.json    <- persistent best-lap database
└── results/                            <- gitignored: TensorBoard logs, .onnx checkpoints, race telemetry
```

---

## Iterating on the training stack

Working on physics, observation shape, or reward design? When the current behavior is good enough to keep around as a regression baseline, freeze it as a `Versions/V<N>/` snapshot and continue editing `Versions/Latest/`. Step-by-step recipe in [`docs/snapshot-version.md`](docs/snapshot-version.md).

---

## License

MIT — see [LICENSE](LICENSE).

---

## Credits

Built by [@sergiolinarescodes](https://github.com/sergiolinarescodes). Uses [Unidad](https://github.com/sergiolinarescodes/unidad), [Reflex DI](https://github.com/gustavopsantos/reflex), and Unity ML-Agents.
