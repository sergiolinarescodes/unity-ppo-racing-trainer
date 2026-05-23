using System;
using Unidad.Core.Grid;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Terrain
{
    /// <summary>
    /// Tile coordinate on the terrain. World axes: X is east, Z is north (forward).
    /// We treat Unidad's <see cref="GridPosition"/> Y as our Z so existing helpers compose.
    /// </summary>
    public readonly record struct TerrainPosition(int X, int Z)
    {
        public static implicit operator GridPosition(TerrainPosition p) => new(p.X, p.Z);
        public static implicit operator TerrainPosition(GridPosition g) => new(g.X, g.Y);

        public Vector2 ToWorldXZ(float cellSize) => new(X * cellSize, Z * cellSize);

        public Vector3 ToWorldCenter(float cellSize, float y) =>
            new(X * cellSize + cellSize * 0.5f, y, Z * cellSize + cellSize * 0.5f);

        public int ManhattanDistanceTo(TerrainPosition other) =>
            Math.Abs(X - other.X) + Math.Abs(Z - other.Z);

        public TerrainPosition North => new(X, Z + 1);
        public TerrainPosition South => new(X, Z - 1);
        public TerrainPosition East => new(X + 1, Z);
        public TerrainPosition West => new(X - 1, Z);

        public override string ToString() => $"({X}, {Z})";
    }
}
