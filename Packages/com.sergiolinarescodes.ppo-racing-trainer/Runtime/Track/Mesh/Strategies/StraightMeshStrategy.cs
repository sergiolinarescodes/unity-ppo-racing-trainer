using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Track
{
    /// <summary>
    /// Single rectangular slab spanning the full footprint, road centred on the
    /// width axis. Width = lane count (always 1 since W=2 shapes were removed).
    /// Length = tile count along +Z. Walls declared in
    /// <see cref="TrackPieceDefinition.Edges"/> are emitted along the matching
    /// long edges. Static kerbs were removed — kerbs come from the dynamic
    /// racing-line kerb service during the ghost-loop preview.
    /// </summary>
    internal sealed class StraightMeshStrategy : ITrackShapeMeshStrategy
    {
        public TrackPieceFamily Family => TrackPieceFamily.Straight;

        public void Build(MeshBuffer buf, TrackPieceDefinition def, TrackPalette palette)
        {
            int W = def.Dimensions.Width;
            int L = def.Dimensions.Length;
            float halfRoadW = W * TrackPieceConstants.LaneHalfWidth;
            float centerX = W * 0.5f;

            float roadX0 = centerX - halfRoadW;
            float roadX1 = centerX + halfRoadW;
            float roadZ0 = 0f;
            float roadZ1 = L;

            MeshPrimitives.AddSlab(buf, roadX0, roadZ0, roadX1, roadZ1,
                TrackPieceConstants.SlabBaseY, TrackPieceConstants.SlabTopY,
                palette.Road, palette.RoadEdge, palette.RoadEdge);

            EmitEdges(buf, def, palette, roadX0, roadX1, roadZ0, roadZ1);
        }

        private static void EmitEdges(MeshBuffer buf, TrackPieceDefinition def, TrackPalette palette,
            float roadX0, float roadX1, float roadZ0, float roadZ1)
        {
            if (def.Edges == null || def.Edges.Count == 0) return;

            float thick = TrackPieceConstants.WallThickness;
            float baseY = TrackPieceConstants.WallYBase;
            float height = TrackPieceConstants.WallHeight;
            Color wallColor = palette.Wall;

            for (int i = 0; i < def.Edges.Count; i++)
            {
                var e = def.Edges[i];

                float t0 = e.StartT;
                float t1 = e.EndT;

                float zStart = Mathf.Lerp(roadZ0, roadZ1, t0);
                float zEnd = Mathf.Lerp(roadZ0, roadZ1, t1);

                switch (e.Anchor)
                {
                    case EdgeAnchor.StraightWest:
                        EmitWestEdge(buf, roadX0, zStart, zEnd, thick, baseY, height, wallColor);
                        break;
                    case EdgeAnchor.StraightEast:
                        EmitEastEdge(buf, roadX1, zStart, zEnd, thick, baseY, height, wallColor);
                        break;
                    case EdgeAnchor.StraightSouth:
                        EmitSouthEdge(buf, roadZ0, Mathf.Lerp(roadX0, roadX1, t0), Mathf.Lerp(roadX0, roadX1, t1),
                            thick, baseY, height, wallColor);
                        break;
                    case EdgeAnchor.StraightNorth:
                        EmitNorthEdge(buf, roadZ1, Mathf.Lerp(roadX0, roadX1, t0), Mathf.Lerp(roadX0, roadX1, t1),
                            thick, baseY, height, wallColor);
                        break;
                }
            }
        }

        private static void EmitWestEdge(MeshBuffer buf, float roadX0, float z0, float z1,
            float thick, float baseY, float height, Color wallColor)
        {
            float shoulder = buf.WallShoulder;
            float wallInnerX = roadX0 - shoulder;
            Vector2 ia = new(wallInnerX, z0);
            Vector2 ib = new(wallInnerX, z1);
            Vector2 oa = new(wallInnerX - thick, z0);
            Vector2 ob = new(wallInnerX - thick, z1);
            MeshPrimitives.AddWallSlab(buf, ia, ib, oa, ob, baseY, height, wallColor);
        }

        private static void EmitEastEdge(MeshBuffer buf, float roadX1, float z0, float z1,
            float thick, float baseY, float height, Color wallColor)
        {
            float shoulder = buf.WallShoulder;
            float wallInnerX = roadX1 + shoulder;
            Vector2 ia = new(wallInnerX, z0);
            Vector2 ib = new(wallInnerX, z1);
            Vector2 oa = new(wallInnerX + thick, z0);
            Vector2 ob = new(wallInnerX + thick, z1);
            MeshPrimitives.AddWallSlab(buf, ia, ib, oa, ob, baseY, height, wallColor);
        }

        private static void EmitSouthEdge(MeshBuffer buf, float roadZ0, float x0, float x1,
            float thick, float baseY, float height, Color wallColor)
        {
            float shoulder = buf.WallShoulder;
            float wallInnerZ = roadZ0 - shoulder;
            Vector2 ia = new(x0, wallInnerZ);
            Vector2 ib = new(x1, wallInnerZ);
            Vector2 oa = new(x0, wallInnerZ - thick);
            Vector2 ob = new(x1, wallInnerZ - thick);
            MeshPrimitives.AddWallSlab(buf, ia, ib, oa, ob, baseY, height, wallColor);
        }

        private static void EmitNorthEdge(MeshBuffer buf, float roadZ1, float x0, float x1,
            float thick, float baseY, float height, Color wallColor)
        {
            float shoulder = buf.WallShoulder;
            float wallInnerZ = roadZ1 + shoulder;
            Vector2 ia = new(x0, wallInnerZ);
            Vector2 ib = new(x1, wallInnerZ);
            Vector2 oa = new(x0, wallInnerZ + thick);
            Vector2 ob = new(x1, wallInnerZ + thick);
            MeshPrimitives.AddWallSlab(buf, ia, ib, oa, ob, baseY, height, wallColor);
        }
    }
}
