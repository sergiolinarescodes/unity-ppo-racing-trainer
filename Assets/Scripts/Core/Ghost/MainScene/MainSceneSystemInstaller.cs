using UnityPpoRacingTrainer.Core.Ghost.Director;
using UnityPpoRacingTrainer.Core.Terrain;
using UnityPpoRacingTrainer.Core.Track.Generation.StarterStrip;
using Reflex.Core;
using Unidad.Core.Bootstrap;
using Unidad.Core.Testing;

namespace UnityPpoRacingTrainer.Core.Ghost.MainScene
{
    public sealed class MainSceneSystemInstaller : ISystemInstaller
    {
        public void Install(ContainerBuilder builder)
        {
            builder.AddSingleton(c => new MainSceneOrchestrator(
                    c.Resolve<ITerrainService>(),
                    c.Resolve<IStarterStripGenerator>(),
                    c.Resolve<IGameSceneDirector>()),
                typeof(IMainSceneOrchestrator));
        }

        public ISystemTestFactory CreateTestFactory() => new MainSceneTestFactory();
    }
}
