using System.Collections.Generic;
using Unidad.Core.Grid;

namespace UnityPpoRacingTrainer.Core.Track.Shape
{
    public readonly record struct ShapePlacementResult(
        bool Success,
        IReadOnlyList<TrackPieceId> PieceIds,
        int InvalidCount,
        int TotalCount);

    /// <summary>
    /// Commits a compound shape: previews first, then places every piece in
    /// shape-list order via <see cref="ITrackPlacementService"/>. All-or-nothing.
    /// <para>
    /// <b>Variants:</b> the <paramref name="variantId"/> is the global default applied
    /// to every piece. Each <see cref="TrackShapePiece"/> may carry its own
    /// <see cref="TrackShapePiece.VariantOverride"/>; when set, that piece commits
    /// with the override regardless of the global value. Authored partial-track
    /// cards rely on this so kerbs/walls survive the round-trip; built-in shapes
    /// from the seeder leave overrides null and inherit the global variant.
    /// </para>
    /// </summary>
    public interface IShapePlacementService
    {
        /// <summary>Place every piece of the shape using its default variant.</summary>
        ShapePlacementResult TryPlaceShape(
            TrackShape shape, GridPosition origin, TrackDirection facing)
            => TryPlaceShape(shape, origin, facing, TrackPieceVariantId.Default, animate: true);

        /// <summary>Place every piece of the shape using the given variant id (drop-from-air animation runs).</summary>
        ShapePlacementResult TryPlaceShape(
            TrackShape shape, GridPosition origin, TrackDirection facing, TrackPieceVariantId variantId)
            => TryPlaceShape(shape, origin, facing, variantId, animate: true);

        /// <summary>Place with explicit control over the drop-from-air animation.
        /// Procedural / bulk-load paths pass <c>animate: false</c>; player-card paths
        /// (deck card, mouse placement, debug authoring) pass <c>animate: true</c>.</summary>
        ShapePlacementResult TryPlaceShape(
            TrackShape shape, GridPosition origin, TrackDirection facing, TrackPieceVariantId variantId, bool animate);
    }
}
