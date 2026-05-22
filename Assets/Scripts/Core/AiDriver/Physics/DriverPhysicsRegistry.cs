using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.AiDriver.Physics.CarCollision;
using UnityPpoRacingTrainer.Core.AiDriver.Physics.Fuel;
using UnityPpoRacingTrainer.Core.AiDriver.Physics.Tires;
using UnityPpoRacingTrainer.Core.AiDriver.Policy;
using Reflex.Core;
using Unidad.Core.Bootstrap;
using Unidad.Core.EventBus;
using Unidad.Core.Testing;

namespace UnityPpoRacingTrainer.Core.AiDriver.Physics
{
    /// <summary>
    /// Single entry-point for attaching a car to every per-driver physics
    /// modifier (tires, fuel, car-car collision). Called by the trainer at
    /// episode begin and by the main-scene ghost service at spawn, so both
    /// surfaces drive against the identical modifier-stack state — the model
    /// trained on these modifiers behaves the same in both contexts.
    ///
    /// Modifier services are optional so this registry works under any subset
    /// of side-systems (legacy scenarios, headless smoke tests). When the
    /// service isn't installed, that channel is skipped silently.
    /// </summary>
    public interface IDriverPhysicsRegistry
    {
        void Register(CarId carId, DriverPersonality personality, float startingLiters);
        void Reset(CarId carId, float startingLiters);
    }

    internal sealed class DriverPhysicsRegistry : IDriverPhysicsRegistry
    {
        private readonly IEventBus _bus;
        private readonly ITirePhysicsService _tire;
        private readonly IFuelService _fuel;
        private readonly ICarCollisionService _collision;

        public DriverPhysicsRegistry(
            IEventBus bus,
            ITirePhysicsService tire = null,
            IFuelService fuel = null,
            ICarCollisionService collision = null)
        {
            _bus = bus;
            _tire = tire;
            _fuel = fuel;
            _collision = collision;
        }

        public void Register(CarId carId, DriverPersonality personality, float startingLiters)
        {
            _tire?.RegisterDriver(carId, personality);
            _tire?.Reset(carId);
            _fuel?.RegisterDriver(carId, personality, startingLiters);
            _collision?.RegisterDriver(carId, personality);
            _bus.Publish(new DriverPersonalityChangedEvent(carId, personality));
        }

        public void Reset(CarId carId, float startingLiters)
        {
            _tire?.Reset(carId);
            _fuel?.Reset(carId, startingLiters);
        }
    }

    public sealed class DriverPhysicsRegistrySystemInstaller : ISystemInstaller
    {
        public void Install(ContainerBuilder builder)
        {
            builder.AddSingleton(c => new DriverPhysicsRegistry(
                    c.Resolve<IEventBus>(),
                    c.TryResolveOptional<ITirePhysicsService>(),
                    c.TryResolveOptional<IFuelService>(),
                    c.TryResolveOptional<ICarCollisionService>()),
                typeof(IDriverPhysicsRegistry));
        }

        public ISystemTestFactory CreateTestFactory() => new DriverPhysicsRegistryTestFactory();
    }

    internal sealed class DriverPhysicsRegistryTestFactory : ISystemTestFactory
    {
        public Type[] TestedServices => new[] { typeof(IDriverPhysicsRegistry) };
        public object CreateForTesting(TestDependencies deps) => null;
        public IEnumerable<ITestScenario> GetScenarios() { yield break; }
    }
}
