using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Track
{
    /// <summary>
    /// Diagonal straight piece — road runs corner-to-corner along the SW ↔ NE
    /// diagonal of the tile (canonical). Emits a rotated rectangular slab whose
    /// long axis is the diagonal. The ribbon mesh paints the visible road on top;
    /// this slab provides the foundation walls / under-deck. Static kerbs were
    /// removed — kerbs come from the dynamic racing-line kerb service.
    /// </summary>
    internal sealed class DiagonalStraightMeshStrategy : ITrackShapeMeshStrategy
    {
        // Heading along (1,1)/√2; left perpendicular = (-1,1)/√2; right = (1,-1)/√2.
        private const float K = 0.7071067811865475f; // 1/√2

        public TrackPieceFamily Family => TrackPieceFamily.DiagonalStraight;

        public void Build(MeshBuffer buf, TrackPieceDefinition def, TrackPalette palette)
        {
            float halfW = TrackPieceConstants.LaneHalfWidth;
            float yLow = TrackPieceConstants.SlabBaseY;
            float yHigh = TrackPieceConstants.SlabTopY;

            float dx = halfW * K;
            float dz = halfW * K;

            // SW corner = (0, 0); NE corner = (1, 1).
            Vector3 sR = new(0f + dx, yHigh, 0f - dz);
            Vector3 sL = new(0f - dx, yHigh, 0f + dz);
            Vector3 eR = new(1f + dx, yHigh, 1f - dz);
            Vector3 eL = new(1f - dx, yHigh, 1f + dz);
            Vector3 sRb = new(sR.x, yLow, sR.z);
            Vector3 sLb = new(sL.x, yLow, sL.z);
            Vector3 eRb = new(eR.x, yLow, eR.z);
            Vector3 eLb = new(eL.x, yLow, eL.z);

            MeshPrimitives.AddQuad(buf, sR, eR, eL, sL, palette.Road);
            MeshPrimitives.AddQuad(buf, sLb, eLb, eRb, sRb, palette.RoadEdge);
            MeshPrimitives.AddQuad(buf, sRb, eRb, eR, sR, palette.RoadEdge);
            MeshPrimitives.AddQuad(buf, eLb, sLb, sL, eL, palette.RoadEdge);
            MeshPrimitives.AddQuad(buf, sLb, sRb, sR, sL, palette.RoadEdge);
            MeshPrimitives.AddQuad(buf, eRb, eLb, eL, eR, palette.RoadEdge);

            EmitDiagonalEdges(buf, def, palette, halfW);
        }

        private static void EmitDiagonalEdges(MeshBuffer buf, TrackPieceDefinition def, TrackPalette palette, float halfW)
        {
            if (def.Edges == null || def.Edges.Count == 0) return;

            float thick = TrackPieceConstants.WallThickness;
            float baseY = TrackPieceConstants.WallYBase;
            float height = TrackPieceConstants.WallHeight;
            float shoulder = buf.WallShoulder;
            Color wallColor = palette.Wall;

            for (int i = 0; i < def.Edges.Count; i++)
            {
                var e = def.Edges[i];
                int side;
                if (e.Anchor == EdgeAnchor.DiagonalRight) side = +1;
                else if (e.Anchor == EdgeAnchor.DiagonalLeft) side = -1;
                else continue;

                EmitDiagonalSideWall(buf, side, e.StartT, e.EndT,
                    halfW, shoulder, thick, baseY, height, wallColor);
            }
        }

        private static void EmitDiagonalSideWall(MeshBuffer buf, int side, float t0, float t1,
            float halfW, float shoulder, float thick, float baseY, float height, Color wallColor)
        {
            // Centerline: (0,0) → (1,1). Position along it: P(t) = (t, t).
            // Perpendicular outward (in XZ): right = (K, -K), left = (-K, +K).
            float pX = side * +K;
            float pZ = side * -K;

            float ax = t0, az = t0;
            float bx = t1, bz = t1;

            float wallInnerOff = halfW + shoulder;
            float wallOuterOff = halfW + shoulder + thick;
            Vector2 ia = new(ax + pX * wallInnerOff, az + pZ * wallInnerOff);
            Vector2 ib = new(bx + pX * wallInnerOff, bz + pZ * wallInnerOff);
            Vector2 oa = new(ax + pX * wallOuterOff, az + pZ * wallOuterOff);
            Vector2 ob = new(bx + pX * wallOuterOff, bz + pZ * wallOuterOff);
            MeshPrimitives.AddWallSlab(buf, ia, ib, oa, ob, baseY, height, wallColor);
        }
    }
}
