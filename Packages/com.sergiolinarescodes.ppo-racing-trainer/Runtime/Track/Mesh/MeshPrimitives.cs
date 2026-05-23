using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Track
{
    /// <summary>
    /// Low-level mesh helpers shared by every track shape strategy. Each helper appends
    /// to a <see cref="MeshBuffer"/> with flat-shaded per-face normals and per-vertex colors.
    /// All inputs are in canonical (north-facing) local coordinates: +X = east, +Z = north.
    /// </summary>
    internal static class MeshPrimitives
    {
        // ---------- Quads / boxes ----------

        /// <summary>Single flat-shaded quad. Corners ordered CCW viewed from the front (normal side).</summary>
        public static void AddQuad(MeshBuffer buf, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color color)
        {
            int i = buf.Vertices.Count;
            buf.Vertices.Add(a); buf.Vertices.Add(b); buf.Vertices.Add(c); buf.Vertices.Add(d);

            var n = Vector3.Cross(b - a, c - a).normalized;
            if (n.sqrMagnitude < 1e-8f) n = Vector3.up;
            buf.Normals.Add(n); buf.Normals.Add(n); buf.Normals.Add(n); buf.Normals.Add(n);
            buf.Colors.Add(color); buf.Colors.Add(color); buf.Colors.Add(color); buf.Colors.Add(color);

            buf.Triangles.Add(i); buf.Triangles.Add(i + 1); buf.Triangles.Add(i + 2);
            buf.Triangles.Add(i); buf.Triangles.Add(i + 2); buf.Triangles.Add(i + 3);
        }

        /// <summary>
        /// Axis-aligned rectangular slab from (x0,z0) to (x1,z1), height [yLow,yHigh].
        /// Emits top, bottom, and 4 side faces — all flat-shaded. When
        /// <paramref name="emitTop"/> is false the top quad is skipped — used by
        /// the ribbon-driven render pipeline where the smoothed cross-piece ribbon
        /// owns the road surface and the per-piece slab is reduced to foundation only.
        /// </summary>
        public static void AddSlab(MeshBuffer buf, float x0, float z0, float x1, float z1,
            float yLow, float yHigh, Color top, Color side, Color bottom,
            bool emitTop = true)
        {
            // Ensure x0<x1, z0<z1 so winding stays consistent.
            if (x0 > x1) (x0, x1) = (x1, x0);
            if (z0 > z1) (z0, z1) = (z1, z0);

            if (emitTop)
            {
                // Top (CCW viewed from +Y)
                AddQuad(buf,
                    new Vector3(x0, yHigh, z0),
                    new Vector3(x1, yHigh, z0),
                    new Vector3(x1, yHigh, z1),
                    new Vector3(x0, yHigh, z1),
                    top);
            }

            // Bottom (CCW viewed from -Y)
            AddQuad(buf,
                new Vector3(x0, yLow, z1),
                new Vector3(x1, yLow, z1),
                new Vector3(x1, yLow, z0),
                new Vector3(x0, yLow, z0),
                bottom);

            // South side z=z0 (CCW viewed from -Z)
            AddQuad(buf,
                new Vector3(x0, yLow, z0),
                new Vector3(x1, yLow, z0),
                new Vector3(x1, yHigh, z0),
                new Vector3(x0, yHigh, z0),
                side);

            // North side z=z1 (CCW viewed from +Z)
            AddQuad(buf,
                new Vector3(x1, yLow, z1),
                new Vector3(x0, yLow, z1),
                new Vector3(x0, yHigh, z1),
                new Vector3(x1, yHigh, z1),
                side);

            // West side x=x0 (CCW viewed from -X)
            AddQuad(buf,
                new Vector3(x0, yLow, z1),
                new Vector3(x0, yLow, z0),
                new Vector3(x0, yHigh, z0),
                new Vector3(x0, yHigh, z1),
                side);

            // East side x=x1 (CCW viewed from +X)
            AddQuad(buf,
                new Vector3(x1, yLow, z0),
                new Vector3(x1, yLow, z1),
                new Vector3(x1, yHigh, z1),
                new Vector3(x1, yHigh, z0),
                side);
        }

        /// <summary>
        /// Constant-thickness slab whose top and bottom faces both slope along +Z.
        /// Use for ramps: bottom y goes (yBottomLow → yBottomHigh) from z0 to z1; top y goes
        /// (yTopLow → yTopHigh) the same way. Constant thickness if (yTopX − yBottomX) is
        /// equal at both ends.
        /// </summary>
        public static void AddSlopedSlab(MeshBuffer buf,
            float x0, float x1, float z0, float z1,
            float yBottomLow, float yBottomHigh,
            float yTopLow, float yTopHigh,
            Color top, Color side, Color bottom,
            bool emitTop = true)
        {
            if (x0 > x1) (x0, x1) = (x1, x0);
            if (z0 > z1)
            {
                (z0, z1) = (z1, z0);
                (yBottomLow, yBottomHigh) = (yBottomHigh, yBottomLow);
                (yTopLow, yTopHigh) = (yTopHigh, yTopLow);
            }

            if (emitTop)
            {
                // Top sloped quad
                AddQuad(buf,
                    new Vector3(x0, yTopLow, z0),
                    new Vector3(x1, yTopLow, z0),
                    new Vector3(x1, yTopHigh, z1),
                    new Vector3(x0, yTopHigh, z1),
                    top);
            }

            // Bottom sloped quad (reversed CCW)
            AddQuad(buf,
                new Vector3(x0, yBottomHigh, z1),
                new Vector3(x1, yBottomHigh, z1),
                new Vector3(x1, yBottomLow, z0),
                new Vector3(x0, yBottomLow, z0),
                bottom);

            // South cap
            AddQuad(buf,
                new Vector3(x0, yBottomLow, z0),
                new Vector3(x1, yBottomLow, z0),
                new Vector3(x1, yTopLow, z0),
                new Vector3(x0, yTopLow, z0),
                side);

            // North cap
            AddQuad(buf,
                new Vector3(x1, yBottomHigh, z1),
                new Vector3(x0, yBottomHigh, z1),
                new Vector3(x0, yTopHigh, z1),
                new Vector3(x1, yTopHigh, z1),
                side);

            // West side (sloped)
            AddQuad(buf,
                new Vector3(x0, yBottomHigh, z1),
                new Vector3(x0, yBottomLow, z0),
                new Vector3(x0, yTopLow, z0),
                new Vector3(x0, yTopHigh, z1),
                side);

            // East side (sloped, mirrored winding)
            AddQuad(buf,
                new Vector3(x1, yBottomLow, z0),
                new Vector3(x1, yBottomHigh, z1),
                new Vector3(x1, yTopHigh, z1),
                new Vector3(x1, yTopLow, z0),
                side);
        }

        // ---------- Arcs ----------

        /// <summary>
        /// Annular sector slab — the procedural "rounded curve". Top + bottom are
        /// triangle-fans between the inner and outer radius; inner and outer side walls
        /// are extruded; the two end caps connect the radii at theta0 and theta1.
        /// theta0/theta1 in radians; sweep direction CCW.
        /// </summary>
        public static void AddArcSlab(MeshBuffer buf,
            Vector2 center, float innerR, float outerR,
            float theta0, float theta1, int segments,
            float yLow, float yHigh,
            Color top, Color side, Color bottom,
            bool emitTop = true)
        {
            if (segments < 2) segments = 2;
            if (innerR > outerR) (innerR, outerR) = (outerR, innerR);

            float dTheta = (theta1 - theta0) / segments;

            // Pre-compute ring vertices (inner / outer at each segment boundary).
            int rings = segments + 1;
            var inner = new Vector2[rings];
            var outer = new Vector2[rings];
            for (int s = 0; s < rings; s++)
            {
                float t = theta0 + dTheta * s;
                float cx = Mathf.Cos(t), cz = Mathf.Sin(t);
                inner[s] = center + new Vector2(cx * innerR, cz * innerR);
                outer[s] = center + new Vector2(cx * outerR, cz * outerR);
            }

            if (emitTop)
            {
                // Top (one quad per segment, CCW viewed from +Y).
                for (int s = 0; s < segments; s++)
                {
                    AddQuad(buf,
                        new Vector3(inner[s].x, yHigh, inner[s].y),
                        new Vector3(outer[s].x, yHigh, outer[s].y),
                        new Vector3(outer[s + 1].x, yHigh, outer[s + 1].y),
                        new Vector3(inner[s + 1].x, yHigh, inner[s + 1].y),
                        top);
                }
            }

            // Bottom (CCW viewed from -Y → reverse winding)
            for (int s = 0; s < segments; s++)
            {
                AddQuad(buf,
                    new Vector3(inner[s + 1].x, yLow, inner[s + 1].y),
                    new Vector3(outer[s + 1].x, yLow, outer[s + 1].y),
                    new Vector3(outer[s].x, yLow, outer[s].y),
                    new Vector3(inner[s].x, yLow, inner[s].y),
                    bottom);
            }

            // Outer wall (face outward, away from center).
            for (int s = 0; s < segments; s++)
            {
                AddQuad(buf,
                    new Vector3(outer[s].x, yLow, outer[s].y),
                    new Vector3(outer[s + 1].x, yLow, outer[s + 1].y),
                    new Vector3(outer[s + 1].x, yHigh, outer[s + 1].y),
                    new Vector3(outer[s].x, yHigh, outer[s].y),
                    side);
            }

            // Inner wall (face inward, toward center → reverse winding).
            for (int s = 0; s < segments; s++)
            {
                AddQuad(buf,
                    new Vector3(inner[s + 1].x, yLow, inner[s + 1].y),
                    new Vector3(inner[s].x, yLow, inner[s].y),
                    new Vector3(inner[s].x, yHigh, inner[s].y),
                    new Vector3(inner[s + 1].x, yHigh, inner[s + 1].y),
                    side);
            }

            // End cap at theta0 (between inner[0] and outer[0]).
            AddQuad(buf,
                new Vector3(inner[0].x, yLow, inner[0].y),
                new Vector3(inner[0].x, yHigh, inner[0].y),
                new Vector3(outer[0].x, yHigh, outer[0].y),
                new Vector3(outer[0].x, yLow, outer[0].y),
                side);

            // End cap at theta1 (mirrored winding).
            AddQuad(buf,
                new Vector3(outer[segments].x, yLow, outer[segments].y),
                new Vector3(outer[segments].x, yHigh, outer[segments].y),
                new Vector3(inner[segments].x, yHigh, inner[segments].y),
                new Vector3(inner[segments].x, yLow, inner[segments].y),
                side);
        }

        /// <summary>
        /// Stripe band sitting on top of an arc slab. Alternates two colors across
        /// <paramref name="stripeCount"/> spans of the angular sweep. Used for piano
        /// (kerb) markings on the outer edge of a curve.
        /// </summary>
        public static void AddArcStripes(MeshBuffer buf,
            Vector2 center, float innerR, float outerR,
            float theta0, float theta1, int stripeCount,
            float yTop,
            Color colorA, Color colorB)
        {
            if (stripeCount < 1) stripeCount = 1;
            if (innerR > outerR) (innerR, outerR) = (outerR, innerR);

            float dTheta = (theta1 - theta0) / stripeCount;
            for (int s = 0; s < stripeCount; s++)
            {
                float t0 = theta0 + dTheta * s;
                float t1 = theta0 + dTheta * (s + 1);
                Color c = (s % 2 == 0) ? colorA : colorB;
                Vector2 ai = center + new Vector2(Mathf.Cos(t0) * innerR, Mathf.Sin(t0) * innerR);
                Vector2 ao = center + new Vector2(Mathf.Cos(t0) * outerR, Mathf.Sin(t0) * outerR);
                Vector2 bo = center + new Vector2(Mathf.Cos(t1) * outerR, Mathf.Sin(t1) * outerR);
                Vector2 bi = center + new Vector2(Mathf.Cos(t1) * innerR, Mathf.Sin(t1) * innerR);
                AddQuad(buf,
                    new Vector3(ai.x, yTop, ai.y),
                    new Vector3(ao.x, yTop, ao.y),
                    new Vector3(bo.x, yTop, bo.y),
                    new Vector3(bi.x, yTop, bi.y),
                    c);
            }
        }

        /// <summary>
        /// Linear stripe band sitting on top of a straight slab. Alternates two colors
        /// across <paramref name="stripeCount"/> spans along the +Z direction. Used for
        /// piano kerbs flanking straights or as a ramp accent.
        /// </summary>
        public static void AddLinearStripes(MeshBuffer buf,
            float x0, float x1, float z0, float z1, int stripeCount,
            float yTop, Color colorA, Color colorB)
        {
            if (stripeCount < 1) stripeCount = 1;
            float dz = (z1 - z0) / stripeCount;
            for (int s = 0; s < stripeCount; s++)
            {
                float a = z0 + dz * s;
                float b = z0 + dz * (s + 1);
                Color c = (s % 2 == 0) ? colorA : colorB;
                AddQuad(buf,
                    new Vector3(x0, yTop, a),
                    new Vector3(x1, yTop, a),
                    new Vector3(x1, yTop, b),
                    new Vector3(x0, yTop, b),
                    c);
            }
        }

        // ---------- Walls ----------

        /// <summary>
        /// Straight wall along an edge — emits collision data + modular F1 catch-fence
        /// barrier placements. No visible mesh quads (replaced by <c>Track/WallBarrier</c>
        /// prefab instances spawned by <see cref="TrackPlacementService"/>).
        /// <para>Collision: one <see cref="WallSegment"/> on the inner edge.</para>
        /// <para>Visual: <c>ceil(|innerB-innerA| / 1.0)</c> <see cref="WallBarrierPlacement"/>
        /// records tiled along the segment. Each barrier sits midway through its slot,
        /// oriented along the inner-edge tangent.</para>
        /// <paramref name="outerA"/>/<paramref name="outerB"/>/<paramref name="color"/> are
        /// retained for API stability — the prefab provides its own visual.
        /// </summary>
        public static void AddWallSlab(MeshBuffer buf,
            Vector2 innerA, Vector2 innerB, Vector2 outerA, Vector2 outerB,
            float baseY, float height, Color color)
        {
            buf.Walls.Add(new WallSegment(innerA, innerB, height, baseY, default));
            EmitBarriersAlongSegment(buf, innerA, innerB);
        }

        // Tiles 1.0u-long barrier modules along a wall segment. For segments
        // shorter than the target length, emits a single shrunken barrier
        // (Length < 1.0). Forward = unit tangent of the segment.
        private const float WallBarrierTargetLength = 1.0f;

        private static void EmitBarriersAlongSegment(MeshBuffer buf, Vector2 a, Vector2 b)
        {
            Vector2 along = b - a;
            float total = along.magnitude;
            if (total < 1e-4f) return;
            Vector2 forward = along / total;
            int slots = Mathf.Max(1, Mathf.RoundToInt(total / WallBarrierTargetLength));
            float slotLen = total / slots;
            for (int i = 0; i < slots; i++)
            {
                float tCenter = (i + 0.5f) / slots;
                Vector2 center = Vector2.Lerp(a, b, tCenter);
                buf.WallBarriers.Add(new WallBarrierPlacement(center, forward, slotLen));
            }
        }

        /// <summary>
        /// Arc wall along an annular sector. Tessellated into <paramref name="segments"/>
        /// slabs that follow the inner edge (radius <paramref name="innerR"/>) and the
        /// outer edge (radius <paramref name="outerR"/>). Each slab emits a 6-face box
        /// + a <see cref="WallSegment"/> tracing the inner ring. <c>innerR &lt; outerR</c>
        /// puts the wall outside the road; <c>innerR &gt; outerR</c> puts it inside.
        /// </summary>
        public static void AddArcWall(MeshBuffer buf,
            Vector2 center, float innerR, float outerR,
            float theta0, float theta1, int segments,
            float baseY, float height, Color color)
        {
            if (segments < 2) segments = 2;
            float dTheta = (theta1 - theta0) / segments;
            for (int s = 0; s < segments; s++)
            {
                float t0 = theta0 + dTheta * s;
                float t1 = theta0 + dTheta * (s + 1);
                Vector2 ia = center + new Vector2(Mathf.Cos(t0) * innerR, Mathf.Sin(t0) * innerR);
                Vector2 ib = center + new Vector2(Mathf.Cos(t1) * innerR, Mathf.Sin(t1) * innerR);
                Vector2 oa = center + new Vector2(Mathf.Cos(t0) * outerR, Mathf.Sin(t0) * outerR);
                Vector2 ob = center + new Vector2(Mathf.Cos(t1) * outerR, Mathf.Sin(t1) * outerR);
                AddWallSlab(buf, ia, ib, oa, ob, baseY, height, color);
            }
        }

        // Static kerb band primitives were removed. Kerbs are now placed dynamically
        // by the racing-line kerb service (Assets/Scripts/Core/Ghost/Kerbs/) during
        // the ghost-loop preview. See QuadKerbZone in MeshBuffer.cs for the zone
        // primitive the dynamic service still uses.
    }
}
