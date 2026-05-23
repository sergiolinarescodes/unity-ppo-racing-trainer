using Reflex.Core;
using Unidad.Core.Abstractions;
using Unidad.Core.Bootstrap;
using Unidad.Core.EventBus;
using Unidad.Core.Factory;
using Unidad.Core.Testing;

namespace UnityPpoRacingTrainer.Core.Terrain.Showcase
{
    public sealed class TerrainShowcaseSystemInstaller : ISystemInstaller
    {
        public void Install(ContainerBuilder builder)
        {
            builder.AddSingleton(c =>
            {
                var bus = c.Resolve<IEventBus>();
                var terrain = c.Resolve<ITerrainService>();
                var meshBuilder = c.Resolve<ITerrainMeshBuilder>();
                var factory = c.Resolve<IGameObjectFactory>();
                return new TerrainShowcaseService(bus, terrain, meshBuilder, factory);
            }, typeof(TerrainShowcaseService), typeof(ITerrainShowcaseService), typeof(ITickable));
        }

        public ISystemTestFactory CreateTestFactory() => new TerrainShowcaseTestFactory();
    }
}
