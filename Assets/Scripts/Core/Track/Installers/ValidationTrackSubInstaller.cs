using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.Ghost.Presentation;
using UnityPpoRacingTrainer.Core.Terrain;
using Reflex.Core;
using Unidad.Core.EventBus;
using Unidad.Core.Factory;

namespace UnityPpoRacingTrainer.Core.Track.Installers
{
    /// <summary>
    /// Validator pipeline (Bounds → Overlap → TerrainCompatibility) plus the
    /// placement service that consumes it. The same pipeline is reused by the
    /// preview service registered in <see cref="ShapeTrackSubInstaller"/>.
    /// Also registers <see cref="ITrackPiecePlacementAnimator"/> so player-card
    /// placements get the drop-from-air animation.
    /// </summary>
    internal sealed class ValidationTrackSubInstaller : ITrackSubInstaller
    {
        public void Install(ContainerBuilder builder)
        {
            builder.AddSingleton(_ =>
            {
                var validators = new List<ITrackPlacementValidator>
                {
                    new BoundsValidator(),
                    new OverlapValidator(),
                    new TerrainCompatibilityValidator()
                };
                return (IReadOnlyList<ITrackPlacementValidator>)validators;
            }, typeof(IReadOnlyList<ITrackPlacementValidator>));

            builder.AddSingleton(c => new TrackPiecePlacementAnimator(
                    c.Resolve<IEventBus>(),
                    c.Resolve<IDropFromAirAnimator>()),
                typeof(ITrackPiecePlacementAnimator),
                typeof(TrackPiecePlacementAnimator),
                typeof(Unidad.Core.Abstractions.ITickable));

            builder.AddSingleton(c => (ITrackPlacementService)new TrackPlacementService(
                    c.Resolve<IEventBus>(),
                    c.Resolve<ITrackPieceCatalog>(),
                    c.Resolve<ITrackPieceMeshBuilder>(),
                    c.Resolve<IReadOnlyList<ITrackPlacementValidator>>(),
                    c.Resolve<ITerrainService>(),
                    c.Resolve<IGameObjectFactory>(),
                    TrackPalette.Default,
                    c.Resolve<ITrackCollisionService>(),
                    c.Resolve<ITrackPiecePlacementAnimator>()),
                typeof(ITrackPlacementService));
        }
    }
}
