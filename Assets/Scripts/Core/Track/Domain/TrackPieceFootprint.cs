using System.Collections.Generic;
using Unidad.Core.Grid;

namespace UnityPpoRacingTrainer.Core.Track
{
    /// <summary>
    /// Set of tile cells a piece occupies. The canonical (north-facing) layout is
    /// a Width × Length rectangle whose anchor tile is local (0,0). The anchor stays
    /// fixed under rotation — other cells rotate around it. So a 2×2 piece facing
    /// East at world origin (5,5) occupies (5,5), (5,4), (6,5), (6,4).
    /// </summary>
    public readonly record struct TrackPieceFootprint(int Width, int Length)
    {
        public int TileCount => Width * Length;

        /// <summary>Cells occupied when the piece is anchored at <paramref name="origin"/> facing <paramref name="facing"/>.</summary>
        public IEnumerable<GridPosition> Tiles(GridPosition origin, TrackDirection facing)
        {
            for (int lz = 0; lz < Length; lz++)
            {
                for (int lx = 0; lx < Width; lx++)
                {
                    var (dx, dz) = RotateOffset(lx, lz, facing);
                    yield return new GridPosition(origin.X + dx, origin.Y + dz);
                }
            }
        }

        /// <summary>Rotates a local (lx, lz) offset by the piece facing. Anchor stays at (0,0).</summary>
        public static (int dx, int dz) RotateOffset(int lx, int lz, TrackDirection facing) => facing switch
        {
            TrackDirection.North => (lx, lz),
            TrackDirection.East => (lz, -lx),
            TrackDirection.South => (-lx, -lz),
            TrackDirection.West => (-lz, lx),
            _ => (lx, lz)
        };
    }
}
