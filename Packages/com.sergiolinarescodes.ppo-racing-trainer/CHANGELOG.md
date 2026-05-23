# Changelog

All notable changes to this package are documented in this file.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.2] - 2026-05-24

### Fixed
- `IsExternalInit` shim is now `public` so consumer assemblies compiling with C# 9 `record` syntax can satisfy the compiler's predefined-type lookup against this package without shipping their own duplicate shim (which would GUID-clash against the package's copy when the consumer was forked from the trainer project).
- Removed two orphan `.meta` files for empty folders (`Runtime/AiDriver/Debug.meta` and `Runtime/Track/Generation/Realistic/Closure.meta`) — they triggered "folder can't be found" warnings on every package import.

## [0.1.1] - 2026-05-23

### Fixed
- Added the previously-missing folder and asset `.meta` files for `Runtime/`, `Editor/`, `Tests/`, `Tests/Editor/`, `Runtime/Resources/`, the three asmdefs, and the markdown / `package.json` files. Without these, Unity treats the git-URL-fetched package as an immutable folder it cannot populate, and silently drops every script inside — every consumer attempting `using UnityPpoRacingTrainer.Core.*` got `CS0246 namespace name could not be found` until this was committed.

## [0.1.0] - 2026-05-23

### Added
- Initial extraction of the AI driver runtime from the `unity-ppo-racing-trainer` Unity project into a standalone UPM package.
- `AiDriverAgent` prefab + baseline ONNX policies under `Runtime/Resources/AiDriver/`.
- Public services for policy, physics, track query, and observation layout under namespace `UnityPpoRacingTrainer.Core`.
- EditMode test suite covering reward shaping, observation writers, and physics scenarios.
