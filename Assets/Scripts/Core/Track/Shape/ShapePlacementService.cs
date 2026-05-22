using System.Collections.Generic;
using Unidad.Core.EventBus;
using Unidad.Core.Grid;

namespace UnityPpoRacingTrainer.Core.Track.Shape
{
    /// <summary>
    /// Validate-all-then-place-all commit. The preview's snapshot semantics match
    /// the commit order, so per-piece <c>TryPlace</c> calls cannot fail after the
    /// preview returned <c>AllValid</c>. No rollback path is needed.
    /// </summary>
    internal sealed class ShapePlacementService : IShapePlacementService
    {
        private readonly IEventBus _eventBus;
        private readonly IShapePreviewService _preview;
        private readonly ITrackPlacementService _placement;

        public ShapePlacementService(
            IEventBus eventBus,
            IShapePreviewService preview,
            ITrackPlacementService placement)
        {
            _eventBus = eventBus;
            _preview = preview;
            _placement = placement;
        }

        public ShapePlacementResult TryPlaceShape(
            TrackShape shape, GridPosition origin, TrackDirection facing)
            => TryPlaceShape(shape, origin, facing, TrackPieceVariantId.Default, animate: true);

        public ShapePlacementResult TryPlaceShape(
            TrackShape shape, GridPosition origin, TrackDirection facing, TrackPieceVariantId variantId)
            => TryPlaceShape(shape, origin, facing, variantId, animate: true);

        public ShapePlacementResult TryPlaceShape(
            TrackShape shape, GridPosition origin, TrackDirection facing, TrackPieceVariantId variantId, bool animate)
        {
            var preview = _preview.Compute(shape, origin, facing);
            if (!preview.AllValid)
            {
                int invalid = 0;
                for (int i = 0; i < preview.Pieces.Count; i++)
                    if (!preview.Pieces[i].Valid) invalid++;

                _eventBus.Publish(new TrackShapePlacementRejectedEvent(
                    shape.Id, origin, facing, invalid, preview.Pieces.Count));
                return new ShapePlacementResult(false, System.Array.Empty<TrackPieceId>(), invalid, preview.Pieces.Count);
            }

            var placed = new List<TrackPieceId>(preview.Pieces.Count);
            for (int i = 0; i < preview.Pieces.Count; i++)
            {
                var p = preview.Pieces[i];
                var pieceVariant = p.VariantOverride ?? variantId;
                var result = _placement.TryPlace(p.PieceType, p.Tile, p.ResolvedFacing, pieceVariant, animate);
                if (!result.Success)
                {
                    UnityEngine.Debug.LogError(
                        $"[ShapePlacementService] Preview/commit divergence on piece {i} ({p.PieceType.Id}@{p.Tile}): {result.Reason}");
                    continue;
                }
                placed.Add(result.Id);
            }

            _eventBus.Publish(new TrackShapePlacedEvent(shape.Id, origin, facing, placed));
            return new ShapePlacementResult(true, placed, 0, preview.Pieces.Count);
        }
    }
}
