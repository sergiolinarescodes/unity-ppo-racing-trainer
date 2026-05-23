using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityPpoRacingTrainer.Core.Track
{
    // -------------------------------------------------------------------------
    // Mesh + collision data, co-located in one file. Keeps the new collision
    // subsystem under one .cs to minimize csproj-regen hits (per project memory:
    // "new .cs files break dotnet build until Unity refreshes — co-locate types
    // in existing files where possible").
    // -------------------------------------------------------------------------

    /// <summary>
    /// Mutable accumulator for procedural mesh generation. Vertices are duplicated per
    /// face so flat-shaded normals stay crisp under the URP/Lit-style vertex-color shader.
    /// Also carries the parallel collision data (walls only — static kerbs were removed;
    /// kerbs are placed dynamically by the racing-line kerb service).
    /// </summary>
    internal sealed class MeshBuffer
    {
        public readonly List<Vector3> Vertices = new();
        public readonly List<Vector3> Normals = new();
        public readonly List<Color> Colors = new();
        public readonly List<int> Triangles = new();

        // Collision side-channel — populated by mesh strategies via MeshPrimitives.
        // Walls are line segments (XZ). Owner ids are populated at placement time,
        // not here (the strategy doesn't know the placed piece's id).
        public readonly List<WallSegment> Walls = new();

        // Visual wall barriers — modular F1 catch-fence prefabs tiled along each
        // wall edge in canonical-local XZ. Placement service spawns one
        // Track/WallBarrier prefab per entry. Wall collision data still lives in
        // Walls above; this list is purely visual.
        public readonly List<WallBarrierPlacement> WallBarriers = new();

        // Active variant-derived wall shoulder for the current Build pass. The
        // builder sets this before calling the strategy so strategies can pull
        // the right Near/Mid offset without a second parameter on every Emit*.
        public float WallShoulder = TrackPieceConstants.WallShoulderNear;

        public void Clear()
        {
            Vertices.Clear();
            Normals.Clear();
            Colors.Clear();
            Triangles.Clear();
            Walls.Clear();
            WallBarriers.Clear();
            WallShoulder = TrackPieceConstants.WallShoulderNear;
        }

        public Mesh ToMesh(string name)
        {
            var mesh = new Mesh { name = name, indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(Vertices);
            mesh.SetNormals(Normals);
            mesh.SetColors(Colors);
            mesh.SetTriangles(Triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }
    }

    /// <summary>
    /// What the mesh builder hands back to the placement service: the visible mesh
    /// plus the parallel canonical-local-space collision data (walls only). Static
    /// kerbs were removed — kerbs are placed dynamically by the racing-line kerb
    /// service during the ghost-loop preview.
    /// </summary>
    public readonly struct MeshBuildResult
    {
        public readonly Mesh Mesh;
        public readonly IReadOnlyList<WallSegment> Walls;
        public readonly IReadOnlyList<WallBarrierPlacement> WallBarriers;

        public MeshBuildResult(Mesh mesh, IReadOnlyList<WallSegment> walls,
            IReadOnlyList<WallBarrierPlacement> wallBarriers)
        {
            Mesh = mesh;
            Walls = walls;
            WallBarriers = wallBarriers;
        }
    }

    /// <summary>
    /// One modular F1-style wall barrier, instanced as a child of the piece GameObject.
    /// Tiled end-to-end along every wall edge. The placement service spawns one
    /// <c>Track/WallBarrier</c> prefab per record at <c>CenterXZ</c>, oriented so the
    /// prefab's local +X aligns with <c>ForwardXZ</c>; localScale.x is set to
    /// <see cref="Length"/> so a chord-sampled curve barrier shrinks to fit its slot.
    /// <para>Unlike walls, these placements carry no collision — collision lives on
    /// <see cref="WallSegment"/>s registered via <see cref="ITrackCollisionService"/>.</para>
    /// </summary>
    public readonly record struct WallBarrierPlacement(
        Vector2 CenterXZ,
        Vector2 ForwardXZ,
        float Length);

    // -------------------------------------------------------------------------
    // Wall + kerb collision primitives (was: Track/Collision/TrackCollisionService.cs).
    // Moved here as part of the new-file consolidation. World-space coords (XZ).
    // -------------------------------------------------------------------------

    /// <summary>
    /// A finite straight-line wall segment, oriented in the XZ plane. Walls are
    /// invisible to the loop chain extractor but visible to the kinematic car
    /// physics, which checks point-vs-segment distance each tick.
    /// </summary>
    public readonly record struct WallSegment(
        Vector2 A,
        Vector2 B,
        float Height,
        float BaseY,
        TrackPieceId Owner);

    /// <summary>
    /// A 2D footprint that grants kerb-grade grip when the car's projected XZ
    /// position is inside it. A kerb is *additive* (better grip than asphalt),
    /// not subtractive — it's the mechanical reward for hugging the inside line.
    /// </summary>
    public interface IKerbZone
    {
        TrackPieceId Owner { get; }
        bool Contains(Vector2 worldXZ);
        Bounds2D BoundsXZ { get; }
    }

    /// <summary>Axis-aligned XZ bounding box used for broad-phase grid binning.</summary>
    public readonly record struct Bounds2D(Vector2 Min, Vector2 Max)
    {
        public bool Contains(Vector2 p) => p.x >= Min.x && p.x <= Max.x && p.y >= Min.y && p.y <= Max.y;
        public bool Overlaps(Bounds2D other)
            => Min.x <= other.Max.x && Max.x >= other.Min.x
            && Min.y <= other.Max.y && Max.y >= other.Min.y;
        public static Bounds2D FromPoints(params Vector2[] pts)
        {
            float minX = float.PositiveInfinity, minY = float.PositiveInfinity;
            float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity;
            for (int i = 0; i < pts.Length; i++)
            {
                if (pts[i].x < minX) minX = pts[i].x;
                if (pts[i].x > maxX) maxX = pts[i].x;
                if (pts[i].y < minY) minY = pts[i].y;
                if (pts[i].y > maxY) maxY = pts[i].y;
            }
            return new Bounds2D(new Vector2(minX, minY), new Vector2(maxX, maxY));
        }
    }

    /// <summary>
    /// Convex-quad kerb zone (any non-degenerate quad). All curve kerbs are
    /// emitted as N tessellated quads to avoid arc math at query time.
    /// </summary>
    public readonly struct QuadKerbZone : IKerbZone
    {
        public readonly Vector2 P0, P1, P2, P3;
        public TrackPieceId Owner { get; }
        public Bounds2D BoundsXZ { get; }

        public QuadKerbZone(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, TrackPieceId owner)
        {
            P0 = p0; P1 = p1; P2 = p2; P3 = p3;
            Owner = owner;
            BoundsXZ = Bounds2D.FromPoints(p0, p1, p2, p3);
        }

        public bool Contains(Vector2 p)
        {
            float c01 = Cross(P1 - P0, p - P0);
            float c12 = Cross(P2 - P1, p - P1);
            float c23 = Cross(P3 - P2, p - P2);
            float c30 = Cross(P0 - P3, p - P3);
            bool allPos = c01 >= 0f && c12 >= 0f && c23 >= 0f && c30 >= 0f;
            bool allNeg = c01 <= 0f && c12 <= 0f && c23 <= 0f && c30 <= 0f;
            return allPos || allNeg;
        }

        private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;
    }

    /// <summary>Result returned by <see cref="ITrackCollisionService.TryFindNearestWall"/>.</summary>
    public readonly record struct WallHit(
        Vector2 ClosestPoint,
        Vector2 Normal,
        float Penetration,
        TrackPieceId Owner);

    /// <summary>
    /// Three-state surface enum used for grip selection. Off-track is decided
    /// independently by <c>TrackQueryService</c> via the centerline projection;
    /// this enum only distinguishes "on a road piece" vs "on a kerb".
    /// </summary>
    public enum SurfaceKind : byte
    {
        Asphalt,
        Kerb
    }

    /// <summary>
    /// Aggregates wall + kerb geometry across every placed track piece into a
    /// uniform-grid broad phase. Queryable per fixed tick by the car sim.
    /// </summary>
    public interface ITrackCollisionService
    {
        void Register(TrackPieceId id, IReadOnlyList<WallSegment> walls, IReadOnlyList<IKerbZone> kerbs);
        void Unregister(TrackPieceId id);
        void Clear();

        bool TryFindNearestWall(Vector3 worldPos, float carRadius, out WallHit hit);
        SurfaceKind SampleSurface(Vector3 worldPos);

        /// <summary>
        /// Cast a 2D ray (XZ plane) from <paramref name="originXZ"/> along
        /// <paramref name="dirXZ"/> (must be unit-length) up to
        /// <paramref name="maxDistance"/>. Returns the distance to the nearest
        /// wall hit, or <paramref name="maxDistance"/> if no hit.
        /// Used by both the policy observation (wall-distance "feeler" rays)
        /// and the debug renderer.
        /// </summary>
        float RaycastWall(Vector2 originXZ, Vector2 dirXZ, float maxDistance);

        IReadOnlyList<WallSegment> AllWalls { get; }
        IReadOnlyList<IKerbZone> AllKerbs { get; }
    }

    internal sealed class TrackCollisionService : ITrackCollisionService
    {
        private const float GridCell = 2.0f;

        private readonly Dictionary<TrackPieceId, PieceEntry> _entries = new();
        private readonly Dictionary<long, List<int>> _wallGrid = new();
        private readonly Dictionary<long, List<int>> _kerbGrid = new();
        private readonly List<WallSegment> _walls = new();
        private readonly List<IKerbZone> _kerbs = new();

        public IReadOnlyList<WallSegment> AllWalls => _walls;
        public IReadOnlyList<IKerbZone> AllKerbs => _kerbs;

        public void Register(TrackPieceId id, IReadOnlyList<WallSegment> walls, IReadOnlyList<IKerbZone> kerbs)
        {
            if ((walls == null || walls.Count == 0) && (kerbs == null || kerbs.Count == 0))
                return;

            if (_entries.ContainsKey(id))
                Unregister(id);

            var entry = new PieceEntry();
            if (walls != null)
                for (int i = 0; i < walls.Count; i++) entry.Walls.Add(walls[i]);
            if (kerbs != null)
                for (int i = 0; i < kerbs.Count; i++) entry.Kerbs.Add(kerbs[i]);

            _entries[id] = entry;
            AppendEntryToIndex(entry);
        }

        public void Unregister(TrackPieceId id)
        {
            if (!_entries.Remove(id)) return;
            RebuildAll();
        }

        public void Clear()
        {
            _entries.Clear();
            _walls.Clear();
            _kerbs.Clear();
            _wallGrid.Clear();
            _kerbGrid.Clear();
        }

        public bool TryFindNearestWall(Vector3 worldPos, float carRadius, out WallHit hit)
        {
            hit = default;
            float bestPen = float.NegativeInfinity;
            bool any = false;

            Vector2 p = new(worldPos.x, worldPos.z);
            float searchR = Mathf.Max(carRadius, GridCell);

            int cx0 = CellAxis(p.x - searchR);
            int cx1 = CellAxis(p.x + searchR);
            int cz0 = CellAxis(p.y - searchR);
            int cz1 = CellAxis(p.y + searchR);

            var seen = new HashSet<int>();
            for (int cz = cz0; cz <= cz1; cz++)
            for (int cx = cx0; cx <= cx1; cx++)
            {
                long key = CellKey(cx, cz);
                if (!_wallGrid.TryGetValue(key, out var list)) continue;
                for (int i = 0; i < list.Count; i++)
                {
                    int idx = list[i];
                    if (!seen.Add(idx)) continue;
                    var w = _walls[idx];
                    Vector2 closest = ClosestPointOnSegment(p, w.A, w.B);
                    Vector2 d = p - closest;
                    float dist = d.magnitude;
                    float pen = carRadius - dist;
                    if (pen <= 0f) continue;
                    if (pen <= bestPen) continue;
                    bestPen = pen;
                    Vector2 n = (dist > 1e-5f) ? d / dist : SegmentNormal(w.A, w.B);
                    hit = new WallHit(closest, n, pen, w.Owner);
                    any = true;
                }
            }
            return any;
        }

        public float RaycastWall(Vector2 originXZ, Vector2 dirXZ, float maxDistance)
        {
            if (maxDistance <= 0f) return 0f;
            float dirLenSq = dirXZ.sqrMagnitude;
            if (dirLenSq < 1e-8f) return maxDistance;
            // Caller is expected to pass a unit vector but normalize defensively.
            if (Mathf.Abs(dirLenSq - 1f) > 1e-3f)
                dirXZ = dirXZ / Mathf.Sqrt(dirLenSq);

            // AABB of the ray for grid traversal.
            Vector2 endXZ = originXZ + dirXZ * maxDistance;
            float minX = Mathf.Min(originXZ.x, endXZ.x);
            float maxX = Mathf.Max(originXZ.x, endXZ.x);
            float minZ = Mathf.Min(originXZ.y, endXZ.y);
            float maxZ = Mathf.Max(originXZ.y, endXZ.y);
            int cx0 = CellAxis(minX), cx1 = CellAxis(maxX);
            int cz0 = CellAxis(minZ), cz1 = CellAxis(maxZ);

            float bestT = maxDistance;
            HashSet<int> seen = null;
            for (int cz = cz0; cz <= cz1; cz++)
            for (int cx = cx0; cx <= cx1; cx++)
            {
                long key = CellKey(cx, cz);
                if (!_wallGrid.TryGetValue(key, out var list)) continue;
                seen ??= new HashSet<int>();
                for (int i = 0; i < list.Count; i++)
                {
                    int idx = list[i];
                    if (!seen.Add(idx)) continue;
                    var w = _walls[idx];
                    if (RaySegmentIntersect(originXZ, dirXZ, bestT, w.A, w.B, out float t)
                        && t < bestT)
                        bestT = t;
                }
            }
            return bestT;
        }

        // Ray (origin + dir*t, t in [0, maxT]) vs segment (A, B). Returns true + t if hit.
        // Both lines parameterized; solve 2x2 linear system. Uses XZ coords.
        private static bool RaySegmentIntersect(Vector2 o, Vector2 d, float maxT,
            Vector2 a, Vector2 b, out float t)
        {
            t = 0f;
            Vector2 s = b - a;
            float denom = d.x * s.y - d.y * s.x;
            if (Mathf.Abs(denom) < 1e-8f) return false; // parallel
            Vector2 oa = a - o;
            float tRay = (oa.x * s.y - oa.y * s.x) / denom;
            float uSeg = (oa.x * d.y - oa.y * d.x) / denom;
            if (tRay < 0f || tRay > maxT) return false;
            if (uSeg < 0f || uSeg > 1f) return false;
            t = tRay;
            return true;
        }

        public SurfaceKind SampleSurface(Vector3 worldPos)
        {
            Vector2 p = new(worldPos.x, worldPos.z);
            int cx = CellAxis(p.x);
            int cz = CellAxis(p.y);
            for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                long key = CellKey(cx + dx, cz + dz);
                if (!_kerbGrid.TryGetValue(key, out var list)) continue;
                for (int i = 0; i < list.Count; i++)
                {
                    var k = _kerbs[list[i]];
                    if (k.BoundsXZ.Contains(p) && k.Contains(p))
                        return SurfaceKind.Kerb;
                }
            }
            return SurfaceKind.Asphalt;
        }

        private void RebuildAll()
        {
            _walls.Clear();
            _kerbs.Clear();
            _wallGrid.Clear();
            _kerbGrid.Clear();
            foreach (var entry in _entries.Values)
                AppendEntryToIndex(entry);
        }

        private void AppendEntryToIndex(PieceEntry entry)
        {
            for (int i = 0; i < entry.Walls.Count; i++)
            {
                int idx = _walls.Count;
                _walls.Add(entry.Walls[i]);
                BinWall(entry.Walls[i], idx);
            }
            for (int i = 0; i < entry.Kerbs.Count; i++)
            {
                int idx = _kerbs.Count;
                _kerbs.Add(entry.Kerbs[i]);
                BinKerb(entry.Kerbs[i], idx);
            }
        }

        private void BinWall(WallSegment w, int idx)
        {
            float minX = Mathf.Min(w.A.x, w.B.x);
            float maxX = Mathf.Max(w.A.x, w.B.x);
            float minZ = Mathf.Min(w.A.y, w.B.y);
            float maxZ = Mathf.Max(w.A.y, w.B.y);
            int cx0 = CellAxis(minX), cx1 = CellAxis(maxX);
            int cz0 = CellAxis(minZ), cz1 = CellAxis(maxZ);
            for (int cz = cz0; cz <= cz1; cz++)
            for (int cx = cx0; cx <= cx1; cx++)
            {
                long key = CellKey(cx, cz);
                if (!_wallGrid.TryGetValue(key, out var list))
                    _wallGrid[key] = list = new List<int>();
                list.Add(idx);
            }
        }

        private void BinKerb(IKerbZone k, int idx)
        {
            var b = k.BoundsXZ;
            int cx0 = CellAxis(b.Min.x), cx1 = CellAxis(b.Max.x);
            int cz0 = CellAxis(b.Min.y), cz1 = CellAxis(b.Max.y);
            for (int cz = cz0; cz <= cz1; cz++)
            for (int cx = cx0; cx <= cx1; cx++)
            {
                long key = CellKey(cx, cz);
                if (!_kerbGrid.TryGetValue(key, out var list))
                    _kerbGrid[key] = list = new List<int>();
                list.Add(idx);
            }
        }

        private static int CellAxis(float v) => Mathf.FloorToInt(v / GridCell);
        private static long CellKey(int cx, int cz) => ((long)cx << 32) ^ (uint)cz;

        private static Vector2 ClosestPointOnSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float lenSq = ab.sqrMagnitude;
            if (lenSq < 1e-8f) return a;
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / lenSq);
            return a + ab * t;
        }

        private static Vector2 SegmentNormal(Vector2 a, Vector2 b)
        {
            Vector2 d = b - a;
            if (d.sqrMagnitude < 1e-8f) return Vector2.right;
            d.Normalize();
            return new Vector2(-d.y, d.x);
        }

        private sealed class PieceEntry
        {
            public readonly List<WallSegment> Walls = new();
            public readonly List<IKerbZone> Kerbs = new();
        }
    }
}
