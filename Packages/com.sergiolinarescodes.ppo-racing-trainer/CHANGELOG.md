# Changelog

All notable changes to this package are documented in this file.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.0] - 2026-05-23

### Added
- Initial extraction of the AI driver runtime from the `unity-ppo-racing-trainer` Unity project into a standalone UPM package.
- `AiDriverAgent` prefab + baseline ONNX policies under `Runtime/Resources/AiDriver/`.
- Public services for policy, physics, track query, and observation layout under namespace `UnityPpoRacingTrainer.Core`.
- EditMode test suite covering reward shaping, observation writers, and physics scenarios.
