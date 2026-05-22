# Contributing

Thanks for your interest in unity-ppo-racing-trainer. This is a personal research project, open-sourced so others can read, fork, and learn from it. PRs are welcome but the bar is high — focus is on a working trainer, not a community framework.

## Before you open a PR

1. **Clone with submodules.** The Unidad framework is a git submodule:
   ```bash
   git clone --recurse-submodules https://github.com/sergiolinarescodes/unity-ppo-racing-trainer.git
   ```
   Forgetting `--recurse-submodules` leaves `Packages/com.unidad.core/` empty and Unity emits cryptic compile errors.

2. **Get the project opening cleanly in Unity 6000.4.0f1.** No other Unity version is supported. ML-Agents 4.0.3 + Reflex DI are pinned by `Packages/manifest.json`.

3. **Run the edit-mode tests locally** before pushing: `Window → General → Test Runner → EditMode → Run All`. CI runs the `Unidad.Core.Tests.*` namespaces only — scenario tests that need authored tracks / ONNX models are skipped there.

4. **Python tooling:** `python tools/setup.py` once. Then `python -c "import torch, mlagents"` should pass.

## What changes are likely to land

- Bug fixes with a repro case (failing test or short scenario).
- Performance improvements with a before/after measurement (TensorBoard graph or stopwatch in a scenario).
- New scenarios that exercise an existing system (place them next to the system, under `*/Scenarios/*Scenario.cs`).
- Doc fixes — typos, broken commands, outdated references.

## What changes are unlikely to land

- Sweeping refactors with no behavioural change (the codebase is small enough to read end-to-end; abstraction layers cost more than they save here).
- Reward-shaping coefficient tweaks without a training-run TensorBoard comparison. Reward changes ship as a frozen `Versions/V<N>/` snapshot — see [`docs/snapshot-version.md`](docs/snapshot-version.md).
- New dependencies in `tools/requirements.txt`. Each transitive dep extends CI install time and the surface area of "works on my Linux box" failures.
- "Modernization" patches that bump Unity, ML-Agents, PyTorch, or Newtonsoft.Json. Versions are pinned for a reason; a bump needs a confirmed training run on the post-bump stack.

## Code conventions

The repo is small enough to read directly. Match what you see. Two things worth calling out:

- **Dependency injection.** Every service is registered by an `*SystemInstaller.cs` and wired by Reflex. Don't `FindObjectOfType`, don't access singletons, don't reach for `Update()` MonoBehaviours.
- **Tunable constants live in `settings.json`.** Reward-shaping coefficients, physics thresholds, episode lengths — all there. The C# side reads them via `ITrainingSettingsService`. A small set of *structural* shape constants are baked in code with a `// Why: ...` comment; if you find one that should be tunable, exposing it requires a paired `settings.json` schema change + JSON-loader update + default value baked into `TrainingSettings.cs`.

## Tests

Two layers:

1. **Convention + unit tests** under `Packages/com.unidad.core/Tests/` and `Assets/Tests/EditMode/`. These run on every push, are fast, and should never depend on Track Editor data or trained ONNX files.
2. **Scenario tests** auto-discovered by `AllSystemScenariosTests`. These boot live Unity scenes. Tests that require user-side fixtures (saved tracks, trained policies, prefab assignments) MUST detect their absence and return `ScenarioVerificationResult.Skip(reason)` rather than `Fail`. CI is allowed to skip — it is not allowed to fail because the contributor didn't carry a private fixture.

If you add a system, add a scenario for it — or decorate the test factory with `[NoScenariosJustified("...")]` and explain why it's covered elsewhere.

## Commit hygiene

- Commits as `sergiolinarescodes` are reserved for the maintainer; please commit under your own identity.
- One concern per commit. Don't bundle a fix and a refactor.
- The commit message body should explain *why*, not just *what*. The diff already shows what changed.

## Reporting bugs

Open an issue with: Unity version, OS, Python version, the command/Editor action that triggered the bug, and the full console output (or the trainer's stderr from `results/<run-id>/`). Stack traces > screenshots.

## Licence

By submitting a PR you agree your contribution is licensed under the project's MIT licence.
