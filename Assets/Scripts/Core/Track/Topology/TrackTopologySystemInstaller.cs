using UnityPpoRacingTrainer.Core.Terrain;
using UnityPpoRacingTrainer.Core.Track.Loop;
using Reflex.Core;
using Unidad.Core.Bootstrap;
using Unidad.Core.EventBus;
using Unidad.Core.Testing;

namespace UnityPpoRacingTrainer.Core.Track.Topology
{
    public sealed class TrackTopologySystemInstaller : ISystemInstaller
    {
        public void Install(ContainerBuilder builder)
        {
            builder.AddSingleton(c => new TrackEndingService(
                    c.Resolve<IEventBus>(),
                    c.Resolve<ITrackPlacementService>(),
                    c.Resolve<ITrackPieceCatalog>(),
                    c.Resolve<IClosedLoopService>(),
                    c.Resolve<ITerrainService>()),
                typeof(ITrackEndingService));
        }

        public ISystemTestFactory CreateTestFactory() => new TrackTopologyTestFactory();
    }
}
