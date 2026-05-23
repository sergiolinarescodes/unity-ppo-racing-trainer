using Reflex.Core;
using Unidad.Core.Bootstrap;
using Unidad.Core.EventBus;
using Unidad.Core.Testing;

namespace UnityPpoRacingTrainer.Core.AiDriver.Race
{
    public sealed class RaceStateSystemInstaller : ISystemInstaller
    {
        public void Install(ContainerBuilder builder)
        {
            builder.AddSingleton(c => new RaceStateService(c.Resolve<IEventBus>()),
                typeof(IRaceStateService));
        }

        public ISystemTestFactory CreateTestFactory() => new RaceStateTestFactory();
    }
}
