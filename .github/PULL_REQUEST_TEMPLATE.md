<!-- Keep this short. The diff carries the detail; this template carries the why. -->

## What this changes
<!-- One paragraph. Don't restate the diff. -->

## Why
<!-- Motivation. If this fixes a bug, link the issue. If this changes reward / observation / physics, paste the TensorBoard comparison or training-run summary. -->

## Risk
<!-- Tick the boxes that apply. Untick the ones that don't — don't leave them all "yes" by reflex. -->

- [ ] Changes reward shaping (will any in-progress ONNX become invalid?)
- [ ] Changes observation layout (`FloatsPerFrame` or per-block layout in `RacingObservationLayout`)
- [ ] Changes physics defaults (`AiDriverPhysicsDefaults`, `CarParameters`, tire / fuel models)
- [ ] Changes `settings.json` schema (new field, removed field, renamed field, changed default)
- [ ] Touches CI / build / Python toolchain
- [ ] Adds a runtime dependency (Unity package or Python lib)

## Tested
<!-- Concrete. "Ran tests" is not concrete; "ran Unidad.Core.Tests + RewardShaperSamplingTests, all pass" is. -->

- [ ] Edit-mode tests pass locally
- [ ] Scenario tests still pass (or correctly Skip on missing fixtures)
- [ ] If touching the trainer: completed at least one short training run (≥ 100k steps) without crashes
- [ ] If touching the dashboard / `settings.json`: confirmed the file still loads and the UI still renders

## Snapshot
<!-- Only if reward / observation / physics changed. Otherwise delete this section. -->

- [ ] Frozen previous canonical state as `Versions/V<N>/` (see `docs/snapshot-version.md`)
