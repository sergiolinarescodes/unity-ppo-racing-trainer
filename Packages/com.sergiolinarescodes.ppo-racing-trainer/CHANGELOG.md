# Changelog

All notable changes to this package are documented in this file.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.4] - 2026-05-24

### Fixed
- `VersionManifestLoader` now falls back to direct `Resources.Load<TextAsset>("AiDriver/Versions/<id>")` calls for known shipped manifest ids (`latest`, `v1`) when `Resources.LoadAll` on the package-shipped subfolder returns empty. In v0.1.3 the loader logged "trying package-shipped Resources" but `LoadAll` returned 0 TextAssets even though Unity's `TextScriptImporter` had successfully imported both JSON files — a Unity 6 quirk with `Resources.LoadAll` on subfolders inside a package's `Resources/` tree. By-name `Resources.Load` is unaffected. Adding a new packaged version means appending its id to `PackagedVersionIds` in `VersionManifestLoader`.

## [0.1.3] - 2026-05-24

### Fixed
- `VersionManifestLoader.LoadAll` now falls back to `Resources/AiDriver/Versions/*.json` when no on-disk `Assets/_Bootstrap/Configs/Versions/` folder is present. Consumers (e.g. the game repo) no longer need to copy training-config files in order to seed `AiDriverVersionRegistry`. Disk entries still win when both sources populate, so the trainer's `/settings` live-edit workflow is unchanged.

### Added
- Canonical `latest.json` and `v1.json` manifests shipped inside the package at `Runtime/Resources/AiDriver/Versions/`. Loaded via `Resources.LoadAll<TextAsset>("AiDriver/Versions")`.
- No-arg `VersionManifestSystemInstaller()` convenience constructor that internally calls `VersionManifestLoader.LoadAll()`. Lets a consumer `installers.Add(new VersionManifestSystemInstaller())` without pre-loading the dict.

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
