using UnityPpoRacingTrainer.Core.Terrain;
using UnityPpoRacingTrainer.Core.Track.Ribbon;
using Reflex.Core;
using Unidad.Core.EventBus;
using Unidad.Core.Factory;

namespace UnityPpoRacingTrainer.Core.Track.Installers
{
    /// <summary>
    /// Cross-piece smoothed road meshes. Subscribes to placement / removal events
    /// to stay in sync with the placement service.
    /// </summary>
    internal sealed class RibbonTrackSubInstaller : ITrackSubInstaller
    {
        public void Install(ContainerBuilder builder)
        {
            builder.AddSingleton(c => (ITrackRibbonService)new TrackRibbonService(
                    c.Resolve<IEventBus>(),
                    c.Resolve<ITrackPlacementService>(),
                    c.Resolve<ITrackPieceCatalog>(),
                    c.Resolve<ITerrainService>(),
                    c.Resolve<IGameObjectFactory>(),
                    TrackPalette.Default),
                typeof(ITrackRibbonService));
        }
    }
}
