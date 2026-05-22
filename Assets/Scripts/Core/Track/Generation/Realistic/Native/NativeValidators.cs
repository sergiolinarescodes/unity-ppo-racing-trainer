using Unity.Collections;
using Unity.Mathematics;

namespace UnityPpoRacingTrainer.Core.Track.Generation.Realistic.Native
{
    // Burst-friendly stateless validators that mirror the managed
    // OverlapValidator + TerrainCompatibilityValidator over native data. Used by
    // the inner search to filter candidates cheaply; the final commit still goes
    // through the managed pipeline (which re-runs the validators authoritatively).
    // Methods are intentionally NOT decorated with [BurstCompile] — Burst rejects
    // vector-by-value across external function boundaries; Burst-compiled jobs
    // (FrontierSearchJobs.ExpandFrontierJob) inline these helpers at use sites.
    public static class NativeValidators
    {
        // Returns true if every footprint cell of the candidate placement is
        // free in `occupancy`. occupancy is a hash set of occupied world cells.
        public static bool ValidateOverlap(
            in NativeArray<ShapeDescriptor> shapes,
            in NativeArray<PieceCell> cells,
            int shapeIndex,
            int2 origin,
            int shapeFacing,
            in NativeHashMap<int2, byte>.ReadOnly occupancy)
        {
            if (shapeIndex < 0 || shapeIndex >= shapes.Length) return false;
            var sd = shapes[shapeIndex];
            for (int i = 0; i < sd.CellCount; i++)
            {
                var c = cells[sd.CellStart + i];
                int2 worldCell = origin + NativeMagnetSnap.RotateCellOffset(c.Dx, c.Dz, shapeFacing);
                if (occupancy.ContainsKey(worldCell)) return false;
            }
            return true;
        }

        // Returns true if every footprint cell respects the terrain rules. Mirrors
        // TerrainCompatibilityValidator: AngleSlope rejects, Flat rejects ramp pieces,
        // CardinalRamp accepts only Straight (and Ramp pieces with matching facing).
        public static bool ValidateTerrain(
            in NativeArray<ShapeDescriptor> shapes,
            in NativeArray<PieceCell> cells,
            int shapeIndex,
            int2 origin,
            int shapeFacing,
            in TerrainSnapshot terrain)
        {
            if (shapeIndex < 0 || shapeIndex >= shapes.Length) return false;
            if (!terrain.Tiles.IsCreated) return false;
            var sd = shapes[shapeIndex];
            for (int i = 0; i < sd.CellCount; i++)
            {
                var c = cells[sd.CellStart + i];
                int2 wc = origin + NativeMagnetSnap.RotateCellOffset(c.Dx, c.Dz, shapeFacing);
                if (wc.x < 0 || wc.y < 0 || wc.x >= terrain.Width || wc.y >= terrain.Depth)
                    return false;
                byte enc = terrain.Tiles[wc.y * terrain.Width + wc.x];
                byte category = TerrainEncoding.Category(enc);
                if (category == TerrainEncoding.CatOob) return false;

                // Allowed-terrain mask check.
                byte mask = c.AllowedTerrain;
                bool catAllowed = false;
                if (category == TerrainEncoding.CatFlat) catAllowed = (mask & 0x01) != 0;
                else if (category == TerrainEncoding.CatCardinalRamp) catAllowed = (mask & 0x02) != 0;
                else if (category == TerrainEncoding.CatAngleSlope) catAllowed = (mask & 0x04) != 0;
                if (!catAllowed) return false;

                if (category == TerrainEncoding.CatAngleSlope) return false;

                byte family = c.Family;
                // Family enum: Straight=0, Curve=1, Ramp=2, DiagonalStraight=3, DiagonalCurve=4
                if (category == TerrainEncoding.CatFlat)
                {
                    if (family == 2) return false;  // Ramp piece on flat tile
                }
                else if (category == TerrainEncoding.CatCardinalRamp)
                {
                    if (family == 2)
                    {
                        int worldFacing = (c.PieceLocalFacing + shapeFacing) & 7;
                        if ((worldFacing & 1) != 0) return false;
                        // Cardinal index: N=0,E=1,S=2,W=3 → worldFacing/2
                        int expectedDir = worldFacing >> 1;
                        int tileDir = TerrainEncoding.RampDir(enc);
                        if (expectedDir != tileDir) return false;
                    }
                    else if (family != 0)
                    {
                        // Only Straight tolerates cardinal-ramp tiles besides matching Ramp.
                        return false;
                    }
                }
            }
            return true;
        }

        // Combined cheap predicate used by the beam-search inner loop.
        public static bool ValidatePlacement(
            in NativeArray<ShapeDescriptor> shapes,
            in NativeArray<PieceCell> cells,
            int shapeIndex,
            int2 origin,
            int shapeFacing,
            in NativeHashMap<int2, byte>.ReadOnly occupancy,
            in TerrainSnapshot terrain)
        {
            return ValidateOverlap(shapes, cells, shapeIndex, origin, shapeFacing, occupancy)
                && ValidateTerrain(shapes, cells, shapeIndex, origin, shapeFacing, terrain);
        }
    }
}
