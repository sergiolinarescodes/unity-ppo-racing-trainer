# Adding a Version Snapshot

The trainer ships a single canonical version (`Latest`). When you iterate on physics, observations, or behavior and want the prior canonical to remain runnable for regression, freeze it as `Versions/V<N>/`.

Numbering starts at `V1` and increments. The number is meaningless beyond ordering — it carries no relation to the maintainer's private development history.

## When to snapshot

- Physics changed in a way that materially shifts car behavior (top speed, grip, brake authority).
- Observation layout changed (any float added, removed, or reordered — breaks ONNX compatibility).
- Reward shaping changed in a way that changes what the policy learns to value.

A simple constant retune that doesn't shift the policy's understanding of the world does NOT require a snapshot.

## Recipe

Assume you're snapshotting the next available number — say `V2` (the previous snapshot was `V1`).

### 1. Bump the enum
`Assets/Scripts/Core/AiDriver/Versions/AiDriverVersion.cs`:
```csharp
public enum AiDriverVersion
{
    V1 = 1,
    V2 = 2,        // <-- newly frozen
    Latest = 100,
}
```

### 2. Create the snapshot directory
Copy the contents of `Versions/V1/` to `Versions/V2/`. Rename namespace `.V1` → `.V2` and class names `V1*` → `V2*` inside every copied file. For physics: bake the *current* `AiDriverPhysicsDefaults.Latest` literal values into `V2PhysicsProfile.cs`. Future edits to `Latest` will diverge from V2.

### 3. Pin the prefab + ONNX
The V2 profile references its own paths:
```csharp
public string BehaviorName => "RacingDriverV2";
public string PrefabResourcePath => "AiDriver/Legacy/AiDriverAgentV2";
public string OnnxResourcePath  => "AiDriver/Policies/RacingDriver-v2";
public string YamlConfigPath    => "Assets/_Bootstrap/Configs/MlAgents/racing_driver_v2.yaml";
public CarParameters PhysicsDefaults => V2PhysicsProfile.PhysicsDefaults;
```

Editor-side steps:
- Duplicate `Assets/Resources/AiDriver/AiDriverAgent.prefab` → `Assets/Resources/AiDriver/Legacy/AiDriverAgentV2.prefab`. Set its `BehaviorParameters.BehaviorName` to `RacingDriverV2`.
- Freeze the trained ONNX as the snapshot's pinned weights:
  ```
  python tools/snapshot_policy_onnx.py --version v2
  ```
  This picks the freshest non-baseline `RacingDriver-*.onnx` from `Assets/Resources/AiDriver/Policies/`, copies it to `RacingDriver-v2.onnx`, and prunes any orphaned step-numbered checkpoints (`RacingDriver-<digits>.onnx`) so the folder stays at one ONNX per snapshot + the rolling `RacingDriver.onnx` + `RacingDriver-baseline.onnx`. Run with `--dry-run` first if you want to preview, or `--source <name>` to freeze a specific file. Re-runs overwrite the snapshot file in place — Unity keeps the existing `.meta` GUID, so prefab refs survive a re-freeze.
- Duplicate the active yaml to `Assets/_Bootstrap/Configs/MlAgents/racing_driver_v2.yaml`; update `behaviors:` to key on `RacingDriverV2`.

### 4. Register in the installer
`Assets/Scripts/Core/AiDriver/Versions/AiDriverVersionsSystemInstaller.cs`:
```csharp
builder.AddSingleton(c => new V2VersionProfile(
        () => c.TryResolveOptional<IRewardShaper>() ?? NullRewardShaper.Instance),
    typeof(V2VersionProfile));
// ...
registry.Register(AiDriverVersion.V2, c.Resolve<V2VersionProfile>());
```

### 5. Re-add the version switch in TrainerBootstrap (if needed)
The trainer's `ConfigureAgent` handles the canonical `AiDriverAgentBehaviour`. If V2's prefab uses a different behaviour class, add a switch arm here.

### 6. Move Latest on
With V2 frozen, you can edit `Versions/Latest/LatestVersionProfile.cs`, `RacingObservationLayout.cs`, `RewardShaper.cs`, and `AiDriverPhysicsDefaults.Latest` freely. The V2 snapshot stays runnable.

## GUID caveats

Unity tracks prefab `m_Script` references by GUID. When you move/rename `.cs` files into `Versions/V2/`, the `.meta` GUIDs stay the same — Unity will resolve them via the relocated file. But if you DUPLICATE the `.cs` file (instead of moving), Unity will see two files with the same GUID and the import will fail.

Always:
- **Move** `.meta` files alongside `.cs` files when relocating.
- For copies (e.g. duplicating `AiDriverAgentBehaviour` → `AiDriverAgentBehaviourV2`), let Unity assign a fresh GUID by deleting the copied `.meta` before re-importing.
- After any version-shuffle, open Unity and verify every prefab's components show their script (no "missing script" yellow warnings).

## Verifying

1. Build the headless trainer.
2. In a test scene, set the `TrainerBootstrap.activeVersion` to `V2`.
3. Press Play. The car should spawn, drive, and read its physics from the frozen profile (verify by setting a unique value in `V2PhysicsProfile`).
4. Run inference with the frozen V2 ONNX and confirm the agent behaves indistinguishably from the original training run.
