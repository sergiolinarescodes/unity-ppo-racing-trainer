using System;
using UnityPpoRacingTrainer.Core.Terrain;

namespace UnityPpoRacingTrainer.Core.Track
{
    /// <summary>
    /// Bitmask over <see cref="TerrainShapeCategory"/> values. Each piece definition
    /// declares which terrain categories its footprint may be placed on. Strict ramp
    /// matching (direction equality) is handled by <c>TerrainCompatibilityValidator</c>
    /// on top of this coarse mask.
    /// </summary>
    [Flags]
    public enum TerrainShapeMask : byte
    {
        None = 0,
        Flat = 1 << 0,
        CardinalRamp = 1 << 1,
        AngleSlope = 1 << 2,
        DiagonalTile = 1 << 3,
        FlatOnly = Flat,
        DiagonalOnly = DiagonalTile,
        FlatAndCardinalRamp = Flat | CardinalRamp,
        All = Flat | CardinalRamp | AngleSlope | DiagonalTile
    }

    public static class TerrainShapeMaskExtensions
    {
        public static bool Includes(this TerrainShapeMask mask, TerrainShapeCategory cat) => cat switch
        {
            TerrainShapeCategory.Flat => (mask & TerrainShapeMask.Flat) != 0,
            TerrainShapeCategory.CardinalRamp => (mask & TerrainShapeMask.CardinalRamp) != 0,
            TerrainShapeCategory.AngleSlope => (mask & TerrainShapeMask.AngleSlope) != 0,
            TerrainShapeCategory.DiagonalTile => (mask & TerrainShapeMask.DiagonalTile) != 0,
            _ => false
        };
    }
}
