using Reflex.Core;
using Unidad.Core.Abstractions;
using Unidad.Core.Bootstrap;
using Unidad.Core.EventBus;
using Unidad.Core.Testing;

namespace UnityPpoRacingTrainer.Core.AiDriver.Physics.CarCollision
{
    public sealed class CarCollisionSystemInstaller : ISystemInstaller
    {
        public void Install(ContainerBuilder builder)
        {
            builder.AddSingleton(c => new CarCollisionService(
                    c.Resolve<IEventBus>(),
                    c.Resolve<ICarSimulationService>()),
                typeof(ICarCollisionService),
                typeof(IFixedTickable));
        }

        public ISystemTestFactory CreateTestFactory() => new CarCollisionTestFactory();
    }
}
