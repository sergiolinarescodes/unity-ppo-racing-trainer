using UnityPpoRacingTrainer.Core.AiDriver.Physics.Modifiers;
using Reflex.Core;
using Unidad.Core.Bootstrap;
using Unidad.Core.EventBus;
using Unidad.Core.Testing;

namespace UnityPpoRacingTrainer.Core.AiDriver.Physics.Draft
{
    public sealed class DraftSystemInstaller : ISystemInstaller
    {
        public void Install(ContainerBuilder builder)
        {
            builder.AddSingleton(c =>
            {
                var service = new DraftService(c.Resolve<IEventBus>());
                c.Resolve<ICarPhysicsModifierAggregator>().Register(service);
                return service;
            }, typeof(IDraftService));
        }

        public ISystemTestFactory CreateTestFactory() => new DraftTestFactory();
    }
}
