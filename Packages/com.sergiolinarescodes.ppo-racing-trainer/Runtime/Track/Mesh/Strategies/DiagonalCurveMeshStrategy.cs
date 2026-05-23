using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Track
{
    /// <summary>
    /// Diagonal-curve family — smooth Hermite-tessellated road between two ports
    /// where at least one port is a tile corner (45° heading). Tessellation count
    /// follows <see cref="TrackPieceConstants.CurveSegmentsSmall"/> so the bend
    /// reads as a curved arc instead of a single twisted quad. The chain extractor
    /// still draws the visible road ribbon over the top — this strategy provides
    /// the foundation slab + edge walls.
    /// Edge markers (<see cref="EdgeAnchor.DiagonalLeft"/> / <see cref="EdgeAnchor.DiagonalRight"/>)
    /// emit walls along the bend, sampled at the same Hermite points as the
    /// foundation strip. Static kerbs were removed — kerbs come from the dynamic
    /// racing-line kerb service.
    /// </summary>
    internal sealed class DiagonalCurveMeshStrategy : ITrackShapeMeshStrategy
    {
        private const float K = 0.7071067811865475f; // 1/√2

        public TrackPieceFamily Family => TrackPieceFamily.DiagonalCurve;

        public void Build(MeshBuffer buf, TrackPieceDefinition def, TrackPalette palette)
        {
            float halfW = TrackPieceConstants.LaneHalfWidth;
            float yLow = TrackPieceConstants.SlabBaseY;
            float yHigh = TrackPieceConstants.SlabTopY;
            int seg = TrackPieceConstants.CurveSegmentsSmall;

            ResolveBendEndpoints(def.Shape, out var sCenter, out var sRight, out var eCenter, out var eRight);

            Vector2 sFwd = new(-sRight.y, sRight.x);
            Vector2 eFwd = new(-eRight.y, eRight.x);
            float chord = Vector2.Distance(sCenter, eCenter);
            Vector2 m0 = sFwd * (1.5f * chord);
            Vector2 m1 = eFwd * (1.5f * chord);

            var samples = new Vector2[seg + 1];
            var rights = new Vector2[seg + 1];
            for (int i = 0; i <= seg; i++)
            {
                float t = i / (float)seg;
                samples[i] = Hermite(sCenter, m0, eCenter, m1, t);
                Vector2 tan = HermiteTangent(sCenter, m0, eCenter, m1, t);
                rights[i] = RightPerp(tan);
            }

            for (int i = 0; i < seg; i++)
            {
                Vector2 c0 = samples[i];
                Vector2 c1 = samples[i + 1];
                Vector2 r0 = rights[i];
                Vector2 r1 = rights[i + 1];
                Vector3 sR = new(c0.x + halfW * r0.x, yHigh, c0.y + halfW * r0.y);
                Vector3 sL = new(c0.x - halfW * r0.x, yHigh, c0.y - halfW * r0.y);
                Vector3 eR = new(c1.x + halfW * r1.x, yHigh, c1.y + halfW * r1.y);
                Vector3 eL = new(c1.x - halfW * r1.x, yHigh, c1.y - halfW * r1.y);
                Vector3 sRb = new(sR.x, yLow, sR.z);
                Vector3 sLb = new(sL.x, yLow, sL.z);
                Vector3 eRb = new(eR.x, yLow, eR.z);
                Vector3 eLb = new(eL.x, yLow, eL.z);

                MeshPrimitives.AddQuad(buf, sR, eR, eL, sL, palette.Road);
                MeshPrimitives.AddQuad(buf, sLb, eLb, eRb, sRb, palette.RoadEdge);
                MeshPrimitives.AddQuad(buf, sRb, eRb, eR, sR, palette.RoadEdge);
                MeshPrimitives.AddQuad(buf, eLb, sLb, sL, eL, palette.RoadEdge);
                if (i == 0)
                    MeshPrimitives.AddQuad(buf, sLb, sRb, sR, sL, palette.RoadEdge);
                if (i == seg - 1)
                    MeshPrimitives.AddQuad(buf, eRb, eLb, eL, eR, palette.RoadEdge);
            }

            EmitDiagEdges(buf, def, palette, halfW, samples, rights);
        }

        private static Vector2 Hermite(Vector2 p0, Vector2 m0, Vector2 p1, Vector2 m1, float t)
        {
            float t2 = t * t;
            float t3 = t2 * t;
            float h00 = 2f * t3 - 3f * t2 + 1f;
            float h10 = t3 - 2f * t2 + t;
            float h01 = -2f * t3 + 3f * t2;
            float h11 = t3 - t2;
            return h00 * p0 + h10 * m0 + h01 * p1 + h11 * m1;
        }

        private static Vector2 HermiteTangent(Vector2 p0, Vector2 m0, Vector2 p1, Vector2 m1, float t)
        {
            float t2 = t * t;
            float dh00 = 6f * t2 - 6f * t;
            float dh10 = 3f * t2 - 4f * t + 1f;
            float dh01 = -6f * t2 + 6f * t;
            float dh11 = 3f * t2 - 2f * t;
            return dh00 * p0 + dh10 * m0 + dh01 * p1 + dh11 * m1;
        }

        private static Vector2 RightPerp(Vector2 forward)
        {
            float mag = forward.magnitude;
            if (mag < 1e-6f) return new Vector2(1f, 0f);
            forward /= mag;
            return new Vector2(forward.y, -forward.x);
        }

        private static void ResolveBendEndpoints(TrackPieceShape shape,
            out Vector2 sCenter, out Vector2 sRight, out Vector2 eCenter, out Vector2 eRight)
        {
            if (shape == TrackPieceShapes.CurveDiagToCardinal_1x1)
            {
                sCenter = new Vector2(1f, 1f);
                sRight = new Vector2(-K, K);
                eCenter = new Vector2(1f, 0.5f);
                eRight = new Vector2(0f, -1f);
            }
            else if (shape == TrackPieceShapes.CurveDiagHairpin_1x1)
            {
                sCenter = new Vector2(1f, 1f);
                sRight = new Vector2(-K, K);
                eCenter = new Vector2(0f, 0.5f);
                eRight = new Vector2(0f, 1f);
            }
            else
            {
                sCenter = new Vector2(0.5f, 0f);
                sRight = new Vector2(1f, 0f);
                eCenter = new Vector2(1f, 1f);
                eRight = new Vector2(K, -K);
            }
        }

        // Emit walls as a strip of segments parallel to the smooth foundation.
        // Each EdgeMarker contributes one wall slab per Hermite segment whose [t0,t1]
        // range overlaps the marker's [StartT, EndT].
        private static void EmitDiagEdges(MeshBuffer buf, TrackPieceDefinition def, TrackPalette palette,
            float halfW, Vector2[] samples, Vector2[] rights)
        {
            if (def.Edges == null || def.Edges.Count == 0) return;

            int seg = samples.Length - 1;
            float thick = TrackPieceConstants.WallThickness;
            float baseY = TrackPieceConstants.WallYBase;
            float height = TrackPieceConstants.WallHeight;
            float shoulder = buf.WallShoulder;
            Color wallColor = palette.Wall;

            for (int i = 0; i < def.Edges.Count; i++)
            {
                var e = def.Edges[i];
                int sideSign;
                if (e.Anchor == EdgeAnchor.DiagonalRight) sideSign = +1;
                else if (e.Anchor == EdgeAnchor.DiagonalLeft) sideSign = -1;
                else continue;

                float t0 = e.StartT, t1 = e.EndT;

                for (int s = 0; s < seg; s++)
                {
                    float st0 = s / (float)seg;
                    float st1 = (s + 1) / (float)seg;
                    if (st1 <= t0 || st0 >= t1) continue;
                    float clipped0 = Mathf.Max(st0, t0);
                    float clipped1 = Mathf.Min(st1, t1);

                    Vector2 c0 = SampleAt(samples, clipped0);
                    Vector2 c1 = SampleAt(samples, clipped1);
                    Vector2 r0 = sideSign * SampleRight(rights, clipped0);
                    Vector2 r1 = sideSign * SampleRight(rights, clipped1);

                    float wallInner = halfW + shoulder;
                    float wallOuter = halfW + shoulder + thick;
                    Vector2 ia = c0 + r0 * wallInner;
                    Vector2 ib = c1 + r1 * wallInner;
                    Vector2 oa = c0 + r0 * wallOuter;
                    Vector2 ob = c1 + r1 * wallOuter;
                    MeshPrimitives.AddWallSlab(buf, ia, ib, oa, ob, baseY, height, wallColor);
                }
            }
        }

        private static Vector2 SampleAt(Vector2[] samples, float t)
        {
            int seg = samples.Length - 1;
            float scaled = Mathf.Clamp01(t) * seg;
            int idx = Mathf.Clamp(Mathf.FloorToInt(scaled), 0, seg - 1);
            float frac = scaled - idx;
            return Vector2.Lerp(samples[idx], samples[idx + 1], frac);
        }

        private static Vector2 SampleRight(Vector2[] rights, float t)
        {
            int seg = rights.Length - 1;
            float scaled = Mathf.Clamp01(t) * seg;
            int idx = Mathf.Clamp(Mathf.FloorToInt(scaled), 0, seg - 1);
            float frac = scaled - idx;
            Vector2 r = Vector2.Lerp(rights[idx], rights[idx + 1], frac);
            float mag = r.magnitude;
            return mag > 1e-6f ? r / mag : r;
        }
    }
}
