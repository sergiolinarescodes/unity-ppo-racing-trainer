using Unidad.Core.Grid;

namespace UnityPpoRacingTrainer.Core.Track
{
    public readonly record struct TrackPiecePlacedEvent(
        TrackPieceId Id,
        TrackPieceShape Shape,
        GridPosition Origin,
        TrackDirection Facing);

    public readonly record struct TrackPieceRemovedEvent(TrackPieceId Id);

    public readonly record struct TrackPiecePlacementRejectedEvent(
        TrackPieceShape Shape,
        GridPosition Origin,
        TrackDirection Facing,
        string Reason);

    public readonly record struct TrackCatalogReadyEvent(int VariantCount);
}
