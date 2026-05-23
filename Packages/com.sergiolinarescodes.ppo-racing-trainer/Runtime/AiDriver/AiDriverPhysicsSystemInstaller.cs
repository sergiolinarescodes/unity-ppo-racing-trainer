using UnityPpoRacingTrainer.Core.AiDriver.Loop;
using UnityPpoRacingTrainer.Core.AiDriver.Physics;
using UnityPpoRacingTrainer.Core.AiDriver.Physics.Modifiers;
using UnityPpoRacingTrainer.Core.Track;
using Reflex.Core;
using Unidad.Core.Abstractions;
using Unidad.Core.Bootstrap;
using Unidad.Core.EventBus;
using Unidad.Core.Testing;

namespace UnityPpoRacingTrainer.Core.AiDriver
{
    /// <summary>
    /// Wires the headless kinematic car simulation. Exposes
    /// <see cref="ICarSimulationService"/> and registers the same instance as
    /// <see cref="IFixedTickable"/> so the bootstrap's TickRunner steps it
    /// at fixed dt. Also resolves the optional
    /// <see cref="ITrackCollisionService"/> so wall hits + kerb grip work, and
    /// <see cref="ICarPhysicsModifierAggregator"/> so optional side-systems
    /// (tires, fuel, drafting) can mutate per-tick parameters without touching
    /// the integrator core.
    /// </summary>
    public sealed class AiDriverPhysicsSystemInstaller : ISystemInstaller
    {
        public void Install(ContainerBuilder builder)
        {
            builder.AddSingleton(_ => new CarPhysicsModifierAggregator(),
                typeof(ICarPhysicsModifierAggregator));

            builder.AddSingleton(c => new CarSimulationService(
                    c.Resolve<IEventBus>(),
                    c.Resolve<ITrackQueryService>(),
                    c.Resolve<ITrackCollisionService>(),
                    c.Resolve<ICarPhysicsModifierAggregator>()),
                typeof(ICarSimulationService),
                typeof(IFixedTickable));
        }

        public ISystemTestFactory CreateTestFactory() => new AiDriverPhysicsTestFactory();
    }
}
