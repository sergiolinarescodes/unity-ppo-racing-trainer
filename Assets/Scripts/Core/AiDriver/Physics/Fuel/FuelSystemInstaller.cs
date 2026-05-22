using UnityPpoRacingTrainer.Core.AiDriver.Physics.Modifiers;
using Reflex.Core;
using Unidad.Core.Bootstrap;
using Unidad.Core.EventBus;
using Unidad.Core.Testing;

namespace UnityPpoRacingTrainer.Core.AiDriver.Physics.Fuel
{
    /// <summary>
    /// Wires the fuel-tracking service and registers it as a per-tick parameter
    /// modifier so fuel mass scales accel/brake/grip and fuel-out forces coast.
    /// </summary>
    public sealed class FuelSystemInstaller : ISystemInstaller
    {
        public void Install(ContainerBuilder builder)
        {
            builder.AddSingleton(c =>
            {
                var service = new FuelService(c.Resolve<IEventBus>());
                c.Resolve<ICarPhysicsModifierAggregator>().Register(service);
                return service;
            }, typeof(IFuelService));
        }

        public ISystemTestFactory CreateTestFactory() => new FuelTestFactory();
    }
}
