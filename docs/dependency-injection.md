# Dependency Injection

The trainer uses [Reflex DI](https://github.com/gustavopsantos/reflex) — a pure-C# constructor-injection container. No service locator, no `FindObjectOfType`, no `Resources.Load`-as-DI. This document is the one-pager for "how do I add a new service?".

## The contract

Every system that wants to plug into the bootstrap MUST implement `ISystemInstaller` (`Packages/com.unidad.core/Runtime/Bootstrap/ISystemInstaller.cs`):

```csharp
public interface ISystemInstaller
{
    void Install(Reflex.Core.ContainerBuilder builder);
    ISystemTestFactory CreateTestFactory();
}
```

Two enforced obligations:

1. **`Install` registers your services** into the container. `TrainerBootstrap` calls every discovered installer in turn at scene-load.
2. **`CreateTestFactory` returns an `ISystemTestFactory`.** The convention test `AllInstallers_HaveTestFactory` fails the build if you forget. The factory advertises which services it covers + a `GetScenarios()` list that `AllSystemScenariosTests` auto-discovers.

If your system genuinely has no UI-facing scenario (e.g. it's a pure utility tested by DOTS unit tests), decorate the factory with `[NoScenariosJustified("explanation")]` instead of inventing a hollow scenario.

## A minimal example

```csharp
namespace UnityPpoRacingTrainer.Core.AiDriver.Foo
{
    public sealed class FooSystemInstaller : ISystemInstaller
    {
        public void Install(ContainerBuilder builder)
        {
            builder.AddSingleton(typeof(IFooService), typeof(FooService));
        }

        public ISystemTestFactory CreateTestFactory() => new FooTestFactory();
    }

    internal sealed class FooService : SystemServiceBase, IFooService
    {
        private readonly IBarService _bar;

        public FooService(IEventBus eventBus, IBarService bar) : base(eventBus)
        {
            _bar = bar;
            Subscribe<SomeEvent>(OnSomeEvent);
        }

        private void OnSomeEvent(SomeEvent e) { /* ... */ }
    }
}
```

That's the whole pattern. `IFooService` is the public surface; `FooService` is `internal sealed` so callers can't reach across the boundary. The convention test `AllServiceImplementations_AreInternal` enforces this.

## Why some dependencies are optional

You'll see constructor parameters like `ITirePhysicsService tires = null` in `RewardShaper`. These are **side-physics modules** — tire wear, fuel, draft, etc. — that can be omitted by curriculum stages that don't exercise them. Stage-0 warm-up runs without tire wear; the reward shaper treats `_tires == null` as "no tire bonus, no overstress penalty". Optional deps let stage progression turn whole systems on and off without rewiring the DI graph.

Use `TryResolveOptional<T>()` (Reflex helper) only inside installers — not inside service constructors. Service constructors should declare optional deps with `= null` defaults so the dependency is *visible* in the signature.

## Lifecycle

- `AddSingleton` — one instance, lives for the lifetime of the container (typically one scene). The default.
- `AddTransient` — one instance per resolve. Rare; use it only for stateless workers.

Reflex does not have a scoped lifetime. Don't try to fake one with `AddSingleton` + manual reset — register a fresh container if you need a fresh scope (`AllSystemScenariosTests` does exactly this between scenarios).

## Adding a new system: checklist

1. Create `Assets/Scripts/Core/<Subsystem>/<Name>/`
2. Add `INameService` (public interface) + `NameService` (internal sealed class)
3. Add `NameSystemInstaller` implementing `ISystemInstaller`
4. Register the installer with `TrainerBootstrap` (`Assets/Scripts/Core/Bootstrap/TrainerBootstrap.cs`) — order matters when one installer needs another's services already in the container
5. Add at least one scenario *or* `[NoScenariosJustified]`
6. Run `Window → General → Test Runner → EditMode → AllInstallers_HaveTestFactory` — should be green

If step 6 is red, you missed step 3 (installer doesn't implement `ISystemInstaller`) or step 5 (factory has zero scenarios with no opt-out).

## Bootstrap order

`TrainerBootstrap` registers installers in a deterministic order. When service A needs service B, A's installer must run *after* B's. If you hit a "no implementation registered for IFoo" error at startup, the cause is almost always installer ordering, not a missing registration.

The current order is in `TrainerBootstrap.cs` — keep new installers grouped with their subsystem (track services together, AI-driver services together, etc.).
