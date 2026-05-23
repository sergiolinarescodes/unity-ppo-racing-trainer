using System.Collections.Generic;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Terrain
{
    /// <summary>
    /// Procedural terrain mesh: per-tile top, per-cube subdivided cliffs, bottom skirt.
    /// Vertices are duplicated per face so flat-shaded normals stay crisp under URP/Lit.
    /// Cliffs are emitted only on the visible (exposed) part of each tile edge — anything
    /// covered by a neighboring cube stack contributes no geometry.
    /// </summary>
    internal sealed class TerrainMeshBuilder : ITerrainMeshBuilder
    {
        private readonly List<Vector3> _verts = new();
        private readonly List<Vector3> _normals = new();
        private readonly List<Color> _colors = new();
        private readonly List<int> _tris = new();

        public Mesh Build(ITerrainService terrain, TerrainPalette palette, TerrainColorMode mode = TerrainColorMode.Palette)
        {
            var mesh = new Mesh { name = "TerrainMesh" };
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            Rebuild(mesh, terrain, palette, mode);
            return mesh;
        }

        public void Rebuild(Mesh mesh, ITerrainService terrain, TerrainPalette palette, TerrainColorMode mode = TerrainColorMode.Palette)
        {
            _verts.Clear();
            _normals.Clear();
            _colors.Clear();
            _tris.Clear();

            if (terrain == null || !terrain.IsInitialized)
            {
                ApplyToMesh(mesh);
                return;
            }

            float c = terrain.CellSize;

            foreach (var pos in terrain.AllTiles)
            {
                EmitTop(terrain, pos, palette, mode, c);
                EmitSides(terrain, pos, palette, c);
            }
            EmitBottomSkirt(terrain, palette);

            ApplyToMesh(mesh);
        }

        // ---- top surface ----

        private void EmitTop(ITerrainService terrain, TerrainPosition pos, TerrainPalette palette, TerrainColorMode mode, float c)
        {
            var corners = terrain.GetCorners(pos);
            Color topColor = mode == TerrainColorMode.Categories
                ? CategoryColor(terrain.GetTile(pos).Shape, palette)
                : palette.Top;
            float x0 = pos.X * c;
            float z0 = pos.Z * c;
            float x1 = x0 + c;
            float z1 = z0 + c;

            // Vertices in world XZ; Y from corner heights. Order: SW, SE, NE, NW.
            var sw = new Vector3(x0, corners.SW, z0);
            var se = new Vector3(x1, corners.SE, z0);
            var ne = new Vector3(x1, corners.NE, z1);
            var nw = new Vector3(x0, corners.NW, z1);

            // Triangles: SW->SE->NE  and  SW->NE->NW (split SW->NE).
            var n1 = SafeNormal(sw, se, ne);
            var n2 = SafeNormal(sw, ne, nw);

            int baseIdx = _verts.Count;
            _verts.Add(sw); _verts.Add(se); _verts.Add(ne);
            _normals.Add(n1); _normals.Add(n1); _normals.Add(n1);
            _colors.Add(topColor); _colors.Add(topColor); _colors.Add(topColor);
            _tris.Add(baseIdx); _tris.Add(baseIdx + 2); _tris.Add(baseIdx + 1);

            baseIdx = _verts.Count;
            _verts.Add(sw); _verts.Add(ne); _verts.Add(nw);
            _normals.Add(n2); _normals.Add(n2); _normals.Add(n2);
            _colors.Add(topColor); _colors.Add(topColor); _colors.Add(topColor);
            _tris.Add(baseIdx); _tris.Add(baseIdx + 2); _tris.Add(baseIdx + 1);
        }

        // ---- side walls (per-cube subdivided, occlusion-aware) ----

        private void EmitSides(ITerrainService terrain, TerrainPosition pos, TerrainPalette palette, float c)
        {
            var corners = terrain.GetCorners(pos);
            float x0 = pos.X * c;
            float z0 = pos.Z * c;
            float x1 = x0 + c;
            float z1 = z0 + c;

            // For each of 4 edges: get the heights of OUR two corners on that edge,
            // and the heights of the NEIGHBOR's two corners on that same shared edge
            // (or 0 for the world boundary). Wall runs from neighbor's max height up
            // to ours. Anything below neighbor's max is occluded.

            // East edge (+X): our SE/NE, neighbor (X+1, Z)'s SW/NW.
            EmitEdgeWall(
                lowerLeft: new Vector3(x1, 0, z0), lowerRight: new Vector3(x1, 0, z1),
                ourLeftH: corners.SE, ourRightH: corners.NE,
                neighborLeftH: NeighborCornerH(terrain, pos.East, true,  false),
                neighborRightH: NeighborCornerH(terrain, pos.East, true,  true),
                outwardNormal: Vector3.right, color: palette.SideDark, palette: palette);

            // West edge (-X): our SW/NW, neighbor (X-1, Z)'s SE/NE.
            EmitEdgeWall(
                lowerLeft: new Vector3(x0, 0, z1), lowerRight: new Vector3(x0, 0, z0),
                ourLeftH: corners.NW, ourRightH: corners.SW,
                neighborLeftH: NeighborCornerH(terrain, pos.West, false, true),
                neighborRightH: NeighborCornerH(terrain, pos.West, false, false),
                outwardNormal: Vector3.left, color: palette.SideDark, palette: palette);

            // North edge (+Z): our NW/NE, neighbor (X, Z+1)'s SW/SE.
            EmitEdgeWall(
                lowerLeft: new Vector3(x1, 0, z1), lowerRight: new Vector3(x0, 0, z1),
                ourLeftH: corners.NE, ourRightH: corners.NW,
                neighborLeftH: NeighborCornerH(terrain, pos.North, true,  false),
                neighborRightH: NeighborCornerH(terrain, pos.North, false, false),
                outwardNormal: Vector3.forward, color: palette.SideLight, palette: palette);

            // South edge (-Z): our SW/SE, neighbor (X, Z-1)'s NW/NE.
            EmitEdgeWall(
                lowerLeft: new Vector3(x0, 0, z0), lowerRight: new Vector3(x1, 0, z0),
                ourLeftH: corners.SW, ourRightH: corners.SE,
                neighborLeftH: NeighborCornerH(terrain, pos.South, false, true),
                neighborRightH: NeighborCornerH(terrain, pos.South, true,  true),
                outwardNormal: Vector3.back, color: palette.SideLight, palette: palette);
        }

        /// <summary>
        /// Returns a corner height of the given (possibly out-of-bounds) tile.
        /// For OOB tiles, treats neighbor as level 0 so perimeter cliffs reach the ground.
        /// The corner picked is one of the tile's 4: parameterized by (eastCorner, northCorner).
        /// </summary>
        private static float NeighborCornerH(ITerrainService terrain, TerrainPosition n, bool eastCorner, bool northCorner)
        {
            if (!terrain.IsInBounds(n)) return 0f;
            var c = terrain.GetCorners(n);
            if (eastCorner && northCorner) return c.NE;
            if (eastCorner && !northCorner) return c.SE;
            if (!eastCorner && northCorner) return c.NW;
            return c.SW;
        }

        /// <summary>
        /// Emits a vertical wall along an edge from `lowerLeft` to `lowerRight` (both at y=0
        /// initially; we lift by neighbor heights on each side). Subdivides into per-cube
        /// (StepHeight = 0.5u) increments. Wall is two-sided in concept but only the outward
        /// face is emitted; the matching neighbor will emit its own face from the other side.
        /// </summary>
        private void EmitEdgeWall(
            Vector3 lowerLeft, Vector3 lowerRight,
            float ourLeftH, float ourRightH,
            float neighborLeftH, float neighborRightH,
            Vector3 outwardNormal, Color color, in TerrainPalette palette)
        {
            float startLeft = neighborLeftH;
            float startRight = neighborRightH;
            float endLeft = ourLeftH;
            float endRight = ourRightH;

            // No wall if we're flush or below neighbor on both ends.
            if (endLeft <= startLeft + TerrainShapeRules.Eps && endRight <= startRight + TerrainShapeRules.Eps)
                return;

            // Walk in 0.5u increments based on the LOW boundary so seams align with cube edges.
            float baseLevel = Mathf.Min(startLeft, startRight);
            float topLevel = Mathf.Max(endLeft, endRight);

            // Snap base to step grid (downward) so the first segment edge sits on a cube line.
            float step = TerrainShapeRules.StepHeight;
            float yLow = Mathf.Floor(baseLevel / step + 1e-3f) * step;

            while (yLow + 1e-3f < topLevel)
            {
                float yHigh = yLow + step;

                float leftBottom = Mathf.Clamp(yLow, startLeft, endLeft);
                float rightBottom = Mathf.Clamp(yLow, startRight, endRight);
                float leftTop = Mathf.Clamp(yHigh, startLeft, endLeft);
                float rightTop = Mathf.Clamp(yHigh, startRight, endRight);

                bool leftCollapsed = leftTop - leftBottom < TerrainShapeRules.Eps;
                bool rightCollapsed = rightTop - rightBottom < TerrainShapeRules.Eps;
                if (!(leftCollapsed && rightCollapsed))
                {
                    var bl = new Vector3(lowerLeft.x, leftBottom, lowerLeft.z);
                    var br = new Vector3(lowerRight.x, rightBottom, lowerRight.z);
                    var tr = new Vector3(lowerRight.x, rightTop, lowerRight.z);
                    var tl = new Vector3(lowerLeft.x, leftTop, lowerLeft.z);

                    int baseIdx = _verts.Count;
                    _verts.Add(bl); _verts.Add(br); _verts.Add(tr); _verts.Add(tl);
                    _normals.Add(outwardNormal); _normals.Add(outwardNormal);
                    _normals.Add(outwardNormal); _normals.Add(outwardNormal);
                    _colors.Add(color); _colors.Add(color); _colors.Add(color); _colors.Add(color);
                    _tris.Add(baseIdx); _tris.Add(baseIdx + 2); _tris.Add(baseIdx + 1);
                    _tris.Add(baseIdx); _tris.Add(baseIdx + 3); _tris.Add(baseIdx + 2);
                }

                yLow = yHigh;
            }
        }

        // ---- bottom skirt ----

        private void EmitBottomSkirt(ITerrainService terrain, TerrainPalette palette)
        {
            float c = terrain.CellSize;
            float minY = 0f;
            for (int z = 0; z < terrain.CornerDepth; z++)
                for (int x = 0; x < terrain.CornerWidth; x++)
                    minY = Mathf.Min(minY, terrain.GetCornerHeight(x, z));

            float w = terrain.Width * c;
            float d = terrain.Depth * c;

            var sw = new Vector3(0f, minY, 0f);
            var se = new Vector3(w, minY, 0f);
            var ne = new Vector3(w, minY, d);
            var nw = new Vector3(0f, minY, d);

            int baseIdx = _verts.Count;
            _verts.Add(sw); _verts.Add(se); _verts.Add(ne); _verts.Add(nw);
            _normals.Add(Vector3.down); _normals.Add(Vector3.down); _normals.Add(Vector3.down); _normals.Add(Vector3.down);
            _colors.Add(palette.SideDark); _colors.Add(palette.SideDark); _colors.Add(palette.SideDark); _colors.Add(palette.SideDark);
            // Bottom faces -Y, so wind opposite to top.
            _tris.Add(baseIdx); _tris.Add(baseIdx + 1); _tris.Add(baseIdx + 2);
            _tris.Add(baseIdx); _tris.Add(baseIdx + 2); _tris.Add(baseIdx + 3);
        }

        // ---- helpers ----

        private static Color CategoryColor(TerrainShape shape, in TerrainPalette palette)
        {
            return shape.GetCategory() switch
            {
                TerrainShapeCategory.Flat => palette.Top,
                TerrainShapeCategory.CardinalRamp => palette.Accent,
                TerrainShapeCategory.AngleSlope => palette.Accent2,
                _ => palette.Top
            };
        }

        private static Vector3 SafeNormal(Vector3 a, Vector3 b, Vector3 c)
        {
            var n = Vector3.Cross(b - a, c - a);
            return n.sqrMagnitude < 1e-12f ? Vector3.up : n.normalized;
        }

        private void ApplyToMesh(Mesh mesh)
        {
            mesh.Clear();
            mesh.SetVertices(_verts);
            mesh.SetNormals(_normals);
            mesh.SetColors(_colors);
            mesh.SetTriangles(_tris, 0);
            mesh.RecalculateBounds();
        }
    }
}
