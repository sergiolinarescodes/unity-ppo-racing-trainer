using UnityPpoRacingTrainer.Core.Terrain;
using UnityPpoRacingTrainer.Core.Track.Loop;
using Reflex.Core;
using Unidad.Core.EventBus;

namespace UnityPpoRacingTrainer.Core.Track.Installers
{
    /// <summary>
    /// Wires <see cref="ClosedLoopService"/>. Watches placement events, rebuilds the
    /// chain on every change, and publishes <see cref="LoopClosedEvent"/> /
    /// <see cref="LoopOpenedEvent"/> so race, AI driver, and betting systems can react.
    /// </summary>
    internal sealed class LoopDetectionTrackSubInstaller : ITrackSubInstaller
    {
        public void Install(ContainerBuilder builder)
        {
            builder.AddSingleton(c => (IClosedLoopService)new ClosedLoopService(
                    c.Resolve<IEventBus>(),
                    c.Resolve<ITrackPlacementService>(),
                    c.Resolve<ITrackPieceCatalog>(),
                    c.Resolve<ITerrainService>()),
                typeof(IClosedLoopService));
        }
    }
}
