using System.Collections.Generic;
using Unidad.Core.Grid;

namespace UnityPpoRacingTrainer.Core.Track.Shape
{
    /// <summary>
    /// Per-piece validity result for one slot in a compound shape preview.
    /// <see cref="VariantOverride"/> mirrors <see cref="TrackShapePiece.VariantOverride"/>
    /// so commit can apply per-piece variants without re-walking the source shape.
    /// </summary>
    public readonly record struct PiecePreview(
        TrackPieceShape PieceType,
        GridPosition Tile,
        TrackDirection ResolvedFacing,
        bool Valid,
        string RejectReason,
        TrackPieceVariantId? VariantOverride = null);

    public readonly record struct ShapePreviewResult(
        GridPosition Origin,
        TrackDirection Facing,
        IReadOnlyList<PiecePreview> Pieces,
        bool AllValid);

    /// <summary>
    /// Computes a non-mutating, per-piece validation snapshot for a compound shape
    /// at a hypothetical origin. The scenario uses this every frame to drive the
    /// green/red ghost preview.
    /// </summary>
    public interface IShapePreviewService
    {
        ShapePreviewResult Compute(TrackShape shape, GridPosition origin, TrackDirection facing);
    }
}
