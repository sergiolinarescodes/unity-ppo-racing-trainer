using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.Terrain;
using UnityPpoRacingTrainer.Core.Track.Shape;
using Reflex.Core;
using Unidad.Core.EventBus;

namespace UnityPpoRacingTrainer.Core.Track.Installers
{
    /// <summary>
    /// Compound-shape layer: shape catalog, preview (read-only validation), commit
    /// (mutating placement), and the cycle service that drives the active shape.
    /// </summary>
    internal sealed class ShapeTrackSubInstaller : ITrackSubInstaller
    {
        public void Install(ContainerBuilder builder)
        {
            builder.AddSingleton(c =>
            {
                var pieces = c.Resolve<ITrackPieceCatalog>();
                var shapes = new TrackShapeCatalog();
                TrackShapeCatalogSeeder.Seed(shapes, pieces);
                return (ITrackShapeCatalog)shapes;
            }, typeof(ITrackShapeCatalog));

            builder.AddSingleton(c => (IShapePreviewService)new ShapePreviewService(
                    c.Resolve<ITrackPieceCatalog>(),
                    c.Resolve<IReadOnlyList<ITrackPlacementValidator>>(),
                    c.Resolve<ITerrainService>(),
                    c.Resolve<ITrackPlacementService>()),
                typeof(IShapePreviewService));

            builder.AddSingleton(c => (IShapePlacementService)new ShapePlacementService(
                    c.Resolve<IEventBus>(),
                    c.Resolve<IShapePreviewService>(),
                    c.Resolve<ITrackPlacementService>()),
                typeof(IShapePlacementService));

            builder.AddSingleton(c => (IShapeCycleService)new ShapeCycleService(
                    c.Resolve<IEventBus>(),
                    c.Resolve<ITrackShapeCatalog>()),
                typeof(IShapeCycleService));
        }
    }
}
