namespace UnityPpoRacingTrainer.Core.Terrain
{
    /// <summary>
    /// Coarse classification of <see cref="TerrainShape"/> for placement / build logic.
    /// </summary>
    public enum TerrainShapeCategory : byte
    {
        /// <summary>All 4 corners equal — buildable, no slope.</summary>
        Flat,
        /// <summary>2 adjacent corners high (RampN/E/S/W) — gentle one-direction slope.</summary>
        CardinalRamp,
        /// <summary>1 corner high (Peak), 1 low (Pit), or diagonal (Saddle) — angled / unsuitable for cleanly-axis-aligned placement.</summary>
        AngleSlope,
        /// <summary>Height-flat tile painted as a 45°-lattice surface — drag-build snaps to NE/SE/SW/NW directions here.</summary>
        DiagonalTile
    }

    public static class TerrainShapeExtensions
    {
        public static TerrainShapeCategory GetCategory(this TerrainShape shape)
        {
            switch (shape)
            {
                case TerrainShape.Flat:
                    return TerrainShapeCategory.Flat;
                case TerrainShape.RampN:
                case TerrainShape.RampE:
                case TerrainShape.RampS:
                case TerrainShape.RampW:
                    return TerrainShapeCategory.CardinalRamp;
                case TerrainShape.DiagonalTile:
                    return TerrainShapeCategory.DiagonalTile;
                default:
                    return TerrainShapeCategory.AngleSlope;
            }
        }

        public static bool IsFlat(this TerrainShape shape) =>
            shape == TerrainShape.Flat;

        public static bool IsCardinalRamp(this TerrainShape shape) =>
            shape == TerrainShape.RampN || shape == TerrainShape.RampE
            || shape == TerrainShape.RampS || shape == TerrainShape.RampW;

        public static bool IsAngleSlope(this TerrainShape shape) =>
            shape.GetCategory() == TerrainShapeCategory.AngleSlope;

        public static bool IsDiagonalTile(this TerrainShape shape) =>
            shape == TerrainShape.DiagonalTile;
    }
}
