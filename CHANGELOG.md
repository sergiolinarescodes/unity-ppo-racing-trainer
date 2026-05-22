# Changelog

All notable changes to this project will be documented in this file.

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); the project loosely follows semver — minor bumps when reward / observation / physics defaults change in a way that breaks a previously-trained ONNX. Patch bumps are safe to apply to an in-progress training run.

## [Unreleased]

### Added
- GitHub Actions CI: Python smoke (`tools/requirements.txt` install + import sanity + `mlagents-learn --help`) and Unity edit-mode tests (gated behind repo-supplied `UNITY_LICENSE` secret).
- `ScenarioVerificationResult.Skip(reason)` + `IsSkipped` for scenarios that need user-side fixtures (authored tracks, ONNX, prefabs). `AllSystemScenariosTests` now calls `Assert.Ignore` for skipped scenarios instead of failing.
- `[NoScenariosJustified(reason)]` opt-out attribute for `ISystemTestFactory` implementations that are covered by other tests (DOTS, unit). `RenderingTestFactory` carries the first instance.
- `RewardShaperSamplingTests` — deterministic-sampling unit tests for `SamplePersonalityForCurrentEpisode` and `SampleStartingLiters`.
- `Assets/Tests/EditMode/UnityPpoRacingTrainer.Core.Tests.asmdef` — first unit-test assembly in the game-code half of the project.
- `docs/dependency-injection.md`, `docs/observation-versioning.md`, `docs/telemetry-schema.md`.

### Changed
- `RewardShaper`: structural shape constants (personality jitter width, tire-overstress threshold + window, draft personality floor/gain, pack-hold floor/gain, etc.) promoted from magic-number literals to named `private const float` fields with a `// Why:` block at the top of the file. Tuning coefficients still live in `settings.json` (`rewardShaper.*`).
- `UCurveCollisionSandboxScenario`: `Object.Destroy` → `DestroyImmediate` (edit-mode safe).
- `TerrainShowcaseScenario`: assertions now match the service's actual 48×48 grid; previously hard-coded to 16×16 and silently failing.
- `RealisticLoopInferenceScenario` + `AuthoredOnlyClosureLoopScenario`: missing test prefab / missing authored tracks now return `Skip(reason)` instead of failing.

### Fixed
- `AllInstallers_HaveTestFactory` no longer fails on `RenderingSystemInstaller` (covered by DOTS tests; opt-out attribute carries the justification).

## [0.2.0] — 2026-05-21

### Added
- Python-only tooling; `settings.json` drives every reward const.
- Initial public release. See commit `c4bb8cd` for the full scope.

[Unreleased]: https://github.com/sergiolinarescodes/unity-ppo-racing-trainer/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/sergiolinarescodes/unity-ppo-racing-trainer/releases/tag/v0.2.0
