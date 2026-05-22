namespace UnityPpoRacingTrainer.Core.Terrain
{
    public readonly record struct TerrainBuildOptions(
        int Width,
        int Depth,
        int DefaultLevel,
        float CellSize)
    {
        public static TerrainBuildOptions Default16 => new(16, 16, 0, 1f);
    }
}
