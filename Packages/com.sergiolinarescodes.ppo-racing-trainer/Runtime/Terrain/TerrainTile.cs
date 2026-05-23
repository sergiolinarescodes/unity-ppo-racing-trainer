namespace UnityPpoRacingTrainer.Core.Terrain
{
    /// <summary>
    /// Derived view of a tile. Source of truth lives in the corner-height field on
    /// <see cref="ITerrainService"/>; this struct is a cached, classified read.
    /// </summary>
    public readonly record struct TerrainTile(
        TerrainShape Shape,
        int BaseLevel,
        CornerHeights Corners)
    {
        public float BaseHeight => BaseLevel * TerrainShapeRules.StepHeight;
        public bool IsRamp => Shape != TerrainShape.Flat && Shape != TerrainShape.DiagonalTile;
        public bool IsDiagonal => Shape == TerrainShape.DiagonalTile;
    }
}
