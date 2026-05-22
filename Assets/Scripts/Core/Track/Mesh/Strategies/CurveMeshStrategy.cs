using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Track
{
    /// <summary>
    /// Quarter-arc canonical NE curve: enters from south at the centre of the south
    /// edge, exits east at the centre of the east edge. Arc center sits at the SE
    /// corner of the bounding box for the small (1×1) variant. The 1×2 long-curve
    /// consists of a straight lead-in tile + a small arc tile.
    /// Walls declared in <see cref="TrackPieceDefinition.Edges"/> are emitted along
    /// the inner / outer arc (or along the lead-in for the long curve). Static kerbs
    /// were removed — kerbs come from the dynamic racing-line kerb service.
    /// </summary>
    internal sealed class CurveMeshStrategy : ITrackShapeMeshStrategy
    {
        public TrackPieceFamily Family => TrackPieceFamily.Curve;

        public void Build(MeshBuffer buf, TrackPieceDefinition def, TrackPalette palette)
        {
            if (def.Shape == TrackPieceShapes.Curve_Long_1x2)
            {
                BuildLongCurve(buf, def, palette);
                return;
            }

            int W = def.Dimensions.Width;
            float Rc = def.CurveCenterRadius;
            float halfRoadW = W * TrackPieceConstants.LaneHalfWidth;
            float Ri = Mathf.Max(0.01f, Rc - halfRoadW);
            float Ro = Rc + halfRoadW;
            Vector2 center = new(W, 0f);
            int seg = TrackPieceConstants.CurveSegmentsSmall;

            MeshPrimitives.AddArcSlab(buf, center, Ri, Ro,
                Mathf.PI * 0.5f, Mathf.PI, seg,
                TrackPieceConstants.SlabBaseY, TrackPieceConstants.SlabTopY,
                palette.Road, palette.RoadEdge, palette.RoadEdge);

            EmitArcEdges(buf, def, palette, center, Ri, Ro, Mathf.PI * 0.5f, Mathf.PI);
        }

        private static void BuildLongCurve(MeshBuffer buf, TrackPieceDefinition def, TrackPalette palette)
        {
            float halfRoadW = TrackPieceConstants.LaneHalfWidth;

            // Tile (0,0) — straight lead-in, z = [0, 1].
            float roadX0 = 0.5f - halfRoadW;
            float roadX1 = 0.5f + halfRoadW;
            MeshPrimitives.AddSlab(buf, roadX0, 0f, roadX1, 1f,
                TrackPieceConstants.SlabBaseY, TrackPieceConstants.SlabTopY,
                palette.Road, palette.RoadEdge, palette.RoadEdge);

            // Tile (0,1) — NE quarter-arc with center at (1, 1), R = 0.5.
            Vector2 center = new(1f, 1f);
            float Rc = TrackPieceConstants.CurveRadiusSmall;
            float Ri = Rc - halfRoadW;
            float Ro = Rc + halfRoadW;
            int seg = TrackPieceConstants.CurveSegmentsSmall;
            MeshPrimitives.AddArcSlab(buf, center, Ri, Ro,
                Mathf.PI * 0.5f, Mathf.PI, seg,
                TrackPieceConstants.SlabBaseY, TrackPieceConstants.SlabTopY,
                palette.Road, palette.RoadEdge, palette.RoadEdge);

            // Edge dispatch — long curve markers carry TileIndex (0 = lead-in, 1 = arc).
            if (def.Edges == null) return;

            float thick = TrackPieceConstants.WallThickness;
            float baseY = TrackPieceConstants.WallYBase;
            float height = TrackPieceConstants.WallHeight;
            Color wallColor = palette.Wall;

            for (int i = 0; i < def.Edges.Count; i++)
            {
                var e = def.Edges[i];
                if (e.TileIndex == 0)
                {
                    float t0 = e.StartT, t1 = e.EndT;
                    float zStart = Mathf.Lerp(0f, 1f, t0);
                    float zEnd = Mathf.Lerp(0f, 1f, t1);
                    if (e.Anchor == EdgeAnchor.StraightWest)
                        EmitStraightSideWall(buf, roadX0, zStart, zEnd, -1, thick, baseY, height, wallColor);
                    else if (e.Anchor == EdgeAnchor.StraightEast)
                        EmitStraightSideWall(buf, roadX1, zStart, zEnd, +1, thick, baseY, height, wallColor);
                }
                else if (e.TileIndex == 1)
                {
                    EmitArcEdge(buf, e, center, Ri, Ro, Mathf.PI * 0.5f, Mathf.PI,
                        thick, baseY, height, wallColor, buf.WallShoulder);
                }
            }
        }

        private static void EmitArcEdges(MeshBuffer buf, TrackPieceDefinition def, TrackPalette palette,
            Vector2 center, float Ri, float Ro, float theta0, float theta1)
        {
            if (def.Edges == null || def.Edges.Count == 0) return;
            float thick = TrackPieceConstants.WallThickness;
            float baseY = TrackPieceConstants.WallYBase;
            float height = TrackPieceConstants.WallHeight;
            Color wallColor = palette.RoadEdge;
            float shoulder = buf.WallShoulder;

            for (int i = 0; i < def.Edges.Count; i++)
            {
                var e = def.Edges[i];
                if (e.Anchor != EdgeAnchor.ArcInner && e.Anchor != EdgeAnchor.ArcOuter) continue;
                EmitArcEdge(buf, e, center, Ri, Ro, theta0, theta1, thick, baseY, height, wallColor, shoulder);
            }
        }

        private static void EmitArcEdge(MeshBuffer buf, EdgeMarker e,
            Vector2 center, float Ri, float Ro, float theta0, float theta1,
            float thick, float baseY, float height, Color wallColor, float shoulder)
        {
            float t0 = e.StartT, t1 = e.EndT;
            float a0 = Mathf.Lerp(theta0, theta1, t0);
            float a1 = Mathf.Lerp(theta0, theta1, t1);
            int seg = TrackPieceConstants.CurveSegmentsSmall;

            if (e.Anchor == EdgeAnchor.ArcOuter)
            {
                float wallInner = Ro + shoulder;
                MeshPrimitives.AddArcWall(buf, center, wallInner, wallInner + thick, a0, a1, seg, baseY, height, wallColor);
            }
            else // ArcInner
            {
                float wallInner = Mathf.Max(0.001f, Ri - shoulder);
                float wallOuter = Mathf.Max(0.001f, wallInner - thick);
                // Swap a0/a1 so the wall-barrier prefab's forward direction
                // along the arc points clockwise — prefab's sponsor face
                // then lands toward the road instead of into the apex.
                MeshPrimitives.AddArcWall(buf, center, wallInner, wallOuter, a1, a0, seg, baseY, height, wallColor);
            }
        }

        private static void EmitStraightSideWall(MeshBuffer buf, float roadX, float z0, float z1, int outwardSign,
            float thick, float baseY, float height, Color wallColor)
        {
            float shoulder = buf.WallShoulder;
            float wallInnerX = roadX + outwardSign * shoulder;
            Vector2 ia = new(wallInnerX, z0);
            Vector2 ib = new(wallInnerX, z1);
            Vector2 oa = new(wallInnerX + outwardSign * thick, z0);
            Vector2 ob = new(wallInnerX + outwardSign * thick, z1);
            MeshPrimitives.AddWallSlab(buf, ia, ib, oa, ob, baseY, height, wallColor);
        }
    }
}
