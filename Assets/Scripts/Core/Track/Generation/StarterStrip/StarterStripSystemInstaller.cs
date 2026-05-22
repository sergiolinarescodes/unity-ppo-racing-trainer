using UnityPpoRacingTrainer.Core.Terrain;
using UnityPpoRacingTrainer.Core.Track.Topology;
using Reflex.Core;
using Unidad.Core.Bootstrap;
using Unidad.Core.EventBus;
using Unidad.Core.Testing;

namespace UnityPpoRacingTrainer.Core.Track.Generation.StarterStrip
{
    public sealed class StarterStripSystemInstaller : ISystemInstaller
    {
        // Canonical 1x1 straight Id from the seeded catalog. If a project ships
        // a different default straight, override before resolving.
        public static TrackPieceShape DefaultStraightShape = TrackPieceShapes.Straight_1x1;

        public void Install(ContainerBuilder builder)
        {
            builder.AddSingleton(c => new StarterStripGenerator(
                    c.Resolve<IEventBus>(),
                    c.Resolve<ITrackPlacementService>(),
                    c.Resolve<ITrackEndingService>(),
                    c.Resolve<ITerrainService>(),
                    DefaultStraightShape),
                typeof(IStarterStripGenerator));
        }

        public ISystemTestFactory CreateTestFactory() => new StarterStripTestFactory();
    }
}
