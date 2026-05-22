using System.Collections.Generic;
using Unidad.Core.Grid;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Terrain
{
    /// <summary>
    /// The terrain service. Owns the corner-height field and exposes tile-level
    /// queries plus a sampling API used by track/object placement.
    ///
    /// Heights are integer multiples of <see cref="TerrainShapeRules.StepHeight"/> (0.5u).
    /// Tile shapes are derived from 4 corner heights and constrained to Flat or
    /// one of 4 cardinal Ramps. Edits are transactional — invalid configurations
    /// (saddle / spike / 3-up) cause a rollback and a <see cref="TerrainEditRejectedEvent"/>.
    /// </summary>
    public interface ITerrainService
    {
        bool IsInitialized { get; }
        int Width { get; }
        int Depth { get; }
        float CellSize { get; }
        int CornerWidth { get; }
        int CornerDepth { get; }
        Bounds WorldBounds { get; }

        void Initialize(TerrainBuildOptions options);
        void Reset();

        bool IsInBounds(TerrainPosition pos);
        TerrainTile GetTile(TerrainPosition pos);
        CornerHeights GetCorners(TerrainPosition pos);
        IEnumerable<TerrainPosition> AllTiles { get; }
        IEnumerable<TerrainPosition> GetNeighbors(TerrainPosition pos, NeighborMode mode);

        float GetCornerHeight(int cornerX, int cornerZ);
        bool TrySetCornerHeight(int cornerX, int cornerZ, float height);
        bool TrySetTileFlat(TerrainPosition pos, int level);
        bool TrySetTileRamp(TerrainPosition pos, TerrainShape ramp, int baseLevel);
        /// <summary>
        /// Mark a height-flat tile as a diagonal-lattice surface (or revert).
        /// Tile must currently classify as Flat; rejected otherwise. Refreshes
        /// the tile cache and emits <see cref="TerrainTileChangedEvent"/> when
        /// the painted shape flips.
        /// </summary>
        bool TrySetDiagonalPaint(TerrainPosition pos, bool isDiagonal);
        bool GetDiagonalPaint(TerrainPosition pos);
        /// <summary>
        /// Replace every corner height in one transaction. Validates that every tile
        /// classifies cleanly under the new field. Rolls back on any failure.
        /// `heights` must be sized [CornerWidth, CornerDepth].
        /// </summary>
        bool TrySetAllCorners(float[,] heights);

        float SampleHeight(float worldX, float worldZ);
        Vector3 SampleNormal(float worldX, float worldZ);
        bool TryWorldToTile(float worldX, float worldZ, out TerrainPosition pos);
    }
}
