using Unidad.Core.Grid;

namespace UnityPpoRacingTrainer.Core.Track
{
    /// <summary>
    /// One placed piece on the track grid. <see cref="VariantId"/> selects which
    /// named alternate of the shape's edges/surfaces was used; defaults to
    /// <see cref="TrackPieceVariantId.Default"/> for legacy single-variant pieces.
    /// </summary>
    public readonly record struct TrackPiece(
        TrackPieceId Id,
        TrackPieceShape Shape,
        GridPosition Origin,
        TrackDirection Facing,
        TrackPieceVariantId VariantId = default);
}
