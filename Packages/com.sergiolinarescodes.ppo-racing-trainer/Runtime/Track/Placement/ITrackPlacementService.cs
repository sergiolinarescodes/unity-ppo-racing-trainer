using System.Collections.Generic;
using Unidad.Core.Grid;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Track
{
    /// <summary>
    /// Places, removes, and tracks track pieces on the grid. Runs the registered
    /// <see cref="ITrackPlacementValidator"/> pipeline before spawning a piece;
    /// publishes <see cref="TrackPiecePlacedEvent"/> / <see cref="TrackPieceRemovedEvent"/> /
    /// <see cref="TrackPiecePlacementRejectedEvent"/> via the event bus.
    /// </summary>
    public interface ITrackPlacementService
    {
        /// <summary>Place using the piece's default variant (legacy entry point).
        /// The drop-from-air animation runs if the placement animator is wired.</summary>
        PlacementResult TryPlace(TrackPieceShape shape, GridPosition origin, TrackDirection facing)
            => TryPlace(shape, origin, facing, TrackPieceVariantId.Default, animate: true);

        /// <summary>Place using a specific variant of the piece. Default animates.</summary>
        PlacementResult TryPlace(TrackPieceShape shape, GridPosition origin, TrackDirection facing, TrackPieceVariantId variantId)
            => TryPlace(shape, origin, facing, variantId, animate: true);

        /// <summary>Place with explicit control over the drop-from-air animation.
        /// Procedural generators (starter strip, training loops) pass <c>animate: false</c>
        /// so bootstrap doesn't stall waiting on visual settle. Player-card placements
        /// pass <c>animate: true</c> (or use the legacy overloads).</summary>
        PlacementResult TryPlace(TrackPieceShape shape, GridPosition origin, TrackDirection facing, TrackPieceVariantId variantId, bool animate);

        bool Remove(TrackPieceId id);
        IReadOnlyCollection<TrackPiece> Placed { get; }
        IReadOnlyDictionary<GridPosition, TrackPieceId> Occupancy { get; }
        GameObject GetGameObject(TrackPieceId id);
        bool TryGetPiece(TrackPieceId id, out TrackPiece piece);
        void Clear();
    }
}
