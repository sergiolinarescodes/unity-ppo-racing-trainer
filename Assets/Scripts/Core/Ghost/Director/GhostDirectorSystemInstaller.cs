using UnityPpoRacingTrainer.Core.Ghost.Presentation;
using UnityPpoRacingTrainer.Core.Ghost.Simulation;
using UnityPpoRacingTrainer.Core.Track.Topology;
using Reflex.Core;
using Unidad.Core.Abstractions;
using Unidad.Core.Bootstrap;
using Unidad.Core.EventBus;
using Unidad.Core.Testing;

namespace UnityPpoRacingTrainer.Core.Ghost.Director
{
    public sealed class GhostDirectorSystemInstaller : ISystemInstaller
    {
        public void Install(ContainerBuilder builder)
        {
            builder.AddSingleton(c => new GameSceneDirector(
                    c.Resolve<IEventBus>(),
                    c.Resolve<ITrackEndingService>(),
                    c.Resolve<IGhostDriverService>(),
                    c.Resolve<IGhostCarPresenter>(),
                    c.Resolve<IGhostSpawnAnimator>()),
                typeof(IGameSceneDirector),
                typeof(ITickable));
        }

        public ISystemTestFactory CreateTestFactory() => new GhostDirectorTestFactory();
    }
}
