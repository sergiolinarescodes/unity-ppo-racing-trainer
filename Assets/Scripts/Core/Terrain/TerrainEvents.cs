namespace UnityPpoRacingTrainer.Core.Terrain
{
    public readonly record struct TerrainInitializedEvent(int Width, int Depth, float CellSize);

    public readonly record struct TerrainResetEvent;

    public readonly record struct TerrainTileChangedEvent(
        TerrainPosition Position,
        TerrainShape Shape,
        int BaseLevel);

    public readonly record struct TerrainCornerHeightChangedEvent(
        int CornerX,
        int CornerZ,
        float OldHeight,
        float NewHeight);

    public readonly record struct TerrainEditRejectedEvent(
        TerrainPosition Position,
        TerrainEditRejectReason Reason);

    public enum TerrainEditRejectReason : byte
    {
        OutOfBounds,
        InvalidCornerCombination,
        HeightStepTooLarge,
        NonHalfStepHeight
    }
}
