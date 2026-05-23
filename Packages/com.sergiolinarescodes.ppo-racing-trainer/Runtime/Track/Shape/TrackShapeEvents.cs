using System.Collections.Generic;
using Unidad.Core.Grid;

namespace UnityPpoRacingTrainer.Core.Track.Shape
{
    public readonly record struct TrackShapeSelectedEvent(TrackShapeId ShapeId, int Index);

    public readonly record struct TrackShapePlacedEvent(
        TrackShapeId ShapeId,
        GridPosition Origin,
        TrackDirection Facing,
        IReadOnlyList<TrackPieceId> PieceIds);

    public readonly record struct TrackShapePlacementRejectedEvent(
        TrackShapeId ShapeId,
        GridPosition Origin,
        TrackDirection Facing,
        int InvalidCount,
        int TotalCount);
}
