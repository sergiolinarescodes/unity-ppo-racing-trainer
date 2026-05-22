using Reflex.Core;
using Unidad.Core.Bootstrap;
using Unidad.Core.EventBus;
using Unidad.Core.Testing;

namespace UnityPpoRacingTrainer.Core.Terrain
{
    public sealed class TerrainSystemInstaller : ISystemInstaller
    {
        public void Install(ContainerBuilder builder)
        {
            builder.AddSingleton(c => (ITerrainService)new TerrainService(c.Resolve<IEventBus>()),
                typeof(ITerrainService));
            builder.AddSingleton(_ => (ITerrainMeshBuilder)new TerrainMeshBuilder(),
                typeof(ITerrainMeshBuilder));
        }

        public ISystemTestFactory CreateTestFactory() => new TerrainTestFactory();
    }
}
