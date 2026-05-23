# PPO Racing Trainer

Runtime AI racing driver trained with PPO via Unity ML-Agents. Ships the `AiDriverAgent` prefab, ONNX policies, the observation/physics/loop services, and Reflex DI installers for consumption from any Unity 6 project.

The training side of the workflow (Python `tools/`, headless trainer build, circuit library) lives in the host repository — this package contains only what a consumer needs at runtime.

- **Host repo:** https://github.com/sergiolinarescodes/unity-ppo-racing-trainer
- **Unity:** 6000.4.0f1
- **ML-Agents:** 4.0.3 (pinned)

## Install

Add to your project's `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.sergiolinarescodes.ppo-racing-trainer": "https://github.com/sergiolinarescodes/unity-ppo-racing-trainer.git?path=Packages/com.sergiolinarescodes.ppo-racing-trainer#v0.1.0"
  }
}
```

Bump the trailing `#v0.1.0` tag to upgrade.

## Prerequisites

UPM does **not** allow git/file dependencies to be declared inside a package's own `package.json`. Consumers must add the following packages to their **own** `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.gustavopsantos.reflex": "https://github.com/gustavopsantos/reflex.git?path=/Assets/Reflex/#13.0.3",
    "com.unidad.core": "file:com.unidad.core",
    "com.unity.ml-agents": "4.0.3"
  }
}
```

`com.unidad.core` is Sergio's services/DI core. Either clone it as a sibling package under `Packages/com.unidad.core/`, add it as a git submodule, or vendor it via a scoped registry — pick whichever your repo prefers.

## Quickstart

1. Install the package + prerequisites.
2. Add a Reflex `ProjectScope` to your bootstrap scene.
3. In a game scene, attach a `MonoInstaller` that binds the package services and loads the default policy:

```csharp
using Reflex.Core;
using UnityEngine;
using UnityPpoRacingTrainer.Core.AiDriver;
using Unity.MLAgents.Policies;

public sealed class AiDriverIntegrationInstaller : MonoBehaviour, IInstaller
{
    public void InstallBindings(ContainerBuilder builder)
    {
        var policy = Resources.Load<Unity.InferenceEngine.ModelAsset>("AiDriver/Policies/RacingDriver");
        builder.AddSingleton(policy);
        // Bind the package's services. See the trainer repo's installers for the full set.
    }
}
```

4. Instantiate the agent at runtime:

```csharp
var prefab = Resources.Load<GameObject>("AiDriver/AiDriverAgent");
Instantiate(prefab, spawnPoint.position, spawnPoint.rotation);
```

See `Samples~/IntegrationExample/` for a runnable scene.

## What's in the package

- `Runtime/AiDriver/` — agent behaviour, physics, observation layout, policy service, reward shaping interfaces.
- `Runtime/Track/` — track query/projection services consumed by the agent.
- `Runtime/Ghost/`, `Runtime/Terrain/` — supporting runtime systems.
- `Runtime/Resources/AiDriver/` — `AiDriverAgent.prefab`, baked ONNX policies under `Policies/`, driver personality SOs under `Profiles/`.
- `Editor/` — property drawers + asset editors.
- `Tests/Editor/` — EditMode tests.

## License

MIT. See `LICENSE.md`.
