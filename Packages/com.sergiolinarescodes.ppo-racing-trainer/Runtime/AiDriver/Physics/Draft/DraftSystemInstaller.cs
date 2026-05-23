using UnityPpoRacingTrainer.Core.AiDriver.Physics.Modifiers;
using UnityPpoRacingTrainer.Core.AiDriver.Versions;
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
            // Slipstream tuning comes from the active version profile —
            // ManifestBackedVersionProfile reads it from the active
            // manifest's `drafting` section (e.g. latest.json / v1.json).
            builder.AddSingleton(c =>
            {
                var profile = c.Resolve<IAiDriverVersionProfile>();
                var service = new DraftService(c.Resolve<IEventBus>(), profile.Drafting);
                c.Resolve<ICarPhysicsModifierAggregator>().Register(service);
                return service;
            }, typeof(IDraftService));
        }

        public ISystemTestFactory CreateTestFactory() => new DraftTestFactory();
    }
}
