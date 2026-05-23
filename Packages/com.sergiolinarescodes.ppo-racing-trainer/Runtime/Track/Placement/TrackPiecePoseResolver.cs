using UnityPpoRacingTrainer.Core.Terrain;
using Unidad.Core.Grid;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Track
{
    /// <summary>
    /// Computes the world-space pose for a placed track piece. Position = anchor tile
    /// world center, sampled to the terrain height at that point. Rotation = the piece's
    /// yaw composed with a tilt that aligns its up-vector to the terrain normal — so a
    /// piece on a cardinal-ramp tile slopes naturally with the ground, without the piece
    /// definition needing to carry slope data. Scale = the live cell size so cell-local
    /// meshes (built at 1u/cell) stretch to fill whatever grid scale the terrain uses.
    /// Used by both the live commit (<see cref="TrackPlacementService"/>) and the ghost
    /// preview so they look identical.
    /// </summary>
    internal static class TrackPiecePoseResolver
    {
        public static (Vector3 position, Quaternion rotation, float scale) Resolve(
            ITerrainService terrain,
            TrackPieceDefinition def,
            GridPosition origin,
            TrackDirection facing)
        {
            float c = terrain != null && terrain.IsInitialized
                ? terrain.CellSize
                : TrackPieceConstants.CellSize;

            float wx = (origin.X + 0.5f) * c;
            float wz = (origin.Y + 0.5f) * c;
            float wy = 0f;
            Vector3 normal = Vector3.up;

            if (terrain != null && terrain.IsInitialized)
            {
                // On Flat-classified tiles snap to the tile's level height and keep
                // the normal vertical. Without this, bilinear normal sampling at the
                // tile center bleeds from neighboring slope corners and a flat-tile
                // piece picks up a tiny tilt that propagates into kerb/wall children
                // — visible as wobble/skew on flat areas adjacent to ramps.
                bool resolved = false;
                if (terrain.TryWorldToTile(wx, wz, out var tilePos) && terrain.IsInBounds(tilePos))
                {
                    var tile = terrain.GetTile(tilePos);
                    if (tile.Shape == TerrainShape.Flat)
                    {
                        wy = TerrainShapeRules.ToHeight(tile.BaseLevel);
                        // normal stays Vector3.up → tilt = identity.
                        resolved = true;
                    }
                }
                if (!resolved)
                {
                    wy = terrain.SampleHeight(wx, wz);
                    normal = terrain.SampleNormal(wx, wz);
                    if (normal.sqrMagnitude < 1e-6f) normal = Vector3.up;
                }
            }

            var yaw = Quaternion.Euler(0f, facing.YawDegrees(), 0f);
            var tilt = Quaternion.FromToRotation(Vector3.up, normal);
            return (new Vector3(wx, wy, wz), tilt * yaw, c);
        }
    }
}
