using UnityPpoRacingTrainer.Core.AiDriver;
using UnityPpoRacingTrainer.Core.AiDriver.Training;
using UnityPpoRacingTrainer.Core.AiDriver.Training.Stages;
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
            builder.AddSingleton(c => new RaceStateService(
                    c.Resolve<IEventBus>(),
                    c.TryResolveOptional<IActiveStageProfile>(),
                    c.TryResolveOptional<IStageIdProvider>()),
                typeof(IRaceStateService));
        }

        public ISystemTestFactory CreateTestFactory() => new RaceStateTestFactory();
    }
}
