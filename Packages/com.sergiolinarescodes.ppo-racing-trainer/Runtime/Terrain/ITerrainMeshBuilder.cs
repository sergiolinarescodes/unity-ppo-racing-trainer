using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Terrain
{
    public enum TerrainColorMode : byte
    {
        /// <summary>Default: top = palette.Top, sides palette-shaded.</summary>
        Palette,
        /// <summary>Top vertex color encodes <see cref="TerrainShapeCategory"/>: Flat = palette.Top, CardinalRamp = palette.Accent, AngleSlope = palette.Accent2.</summary>
        Categories
    }

    /// <summary>
    /// Builds a procedural mesh from an <see cref="ITerrainService"/> snapshot.
    /// </summary>
    public interface ITerrainMeshBuilder
    {
        Mesh Build(ITerrainService terrain, TerrainPalette palette, TerrainColorMode mode = TerrainColorMode.Palette);
        void Rebuild(Mesh mesh, ITerrainService terrain, TerrainPalette palette, TerrainColorMode mode = TerrainColorMode.Palette);
    }
}
