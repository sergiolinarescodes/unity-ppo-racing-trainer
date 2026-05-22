using UnityPpoRacingTrainer.Core.AiDriver.Config;
using UnityPpoRacingTrainer.Core.AiDriver.Physics.Modifiers;
using Reflex.Core;
using Unidad.Core.Bootstrap;
using Unidad.Core.EventBus;
using Unidad.Core.Testing;

namespace UnityPpoRacingTrainer.Core.AiDriver.Physics.Tires
{
    /// <summary>
    /// Wires the tire-wear physics service and registers it as a modifier so
    /// per-tick grip degradation flows into <c>CarSimulationService</c> without
    /// the integrator knowing about tires.
    /// </summary>
    public sealed class TirePhysicsSystemInstaller : ISystemInstaller
    {
        public void Install(ContainerBuilder builder)
        {
            builder.AddSingleton(c =>
            {
                var service = new TirePhysicsService(
                    c.Resolve<IEventBus>(),
                    c.Resolve<ITrainingSettingsService>());
                c.Resolve<ICarPhysicsModifierAggregator>().Register(service);
                return service;
            }, typeof(ITirePhysicsService));
        }

        public ISystemTestFactory CreateTestFactory() => new TirePhysicsTestFactory();
    }
}
