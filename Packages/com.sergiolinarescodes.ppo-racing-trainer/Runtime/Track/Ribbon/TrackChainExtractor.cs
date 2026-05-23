using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.Track.Geometry;
using UnityPpoRacingTrainer.Core.Track.Ribbon.Spine;
using Unidad.Core.Grid;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Track.Ribbon
{
    /// <summary>
    /// Walks the placed-piece set and groups pieces into ordered chains by matching
    /// world-space port endpoints. Each chain emerges as a list of
    /// <see cref="TrackChainAnchor"/>s ready to feed the spline. Anchor Y is set to
    /// zero — the ribbon mesh builder samples terrain Y per spline sample, so two
    /// chained pieces on different terrain levels still produce a smooth ribbon that
    /// follows the ground.
    ///
    /// The canonical (north-facing) shape of one piece is produced by an
    /// <see cref="ITrackSpineSampler"/> looked up per <see cref="TrackPieceFamily"/>.
    /// Adding a new family = one new strategy class registered in the constructor;
    /// no switch edit here.
    /// </summary>
    public sealed class TrackChainExtractor
    {
        private readonly ITrackPieceCatalog _catalog;
        private readonly Dictionary<TrackPieceFamily, ITrackSpineSampler> _samplers;

        public TrackChainExtractor(ITrackPieceCatalog catalog)
            : this(catalog, DefaultSamplers()) { }

        public TrackChainExtractor(ITrackPieceCatalog catalog, IEnumerable<ITrackSpineSampler> samplers)
        {
            _catalog = catalog;
            _samplers = new Dictionary<TrackPieceFamily, ITrackSpineSampler>();
            foreach (var s in samplers) _samplers[s.Family] = s;
        }

        public static IEnumerable<ITrackSpineSampler> DefaultSamplers() => new ITrackSpineSampler[]
        {
            new StraightSpineStrategy(),
            new CurveSpineStrategy(),
            new RampSpineStrategy(),
            new DiagonalStraightSpineStrategy(),
            new DiagonalCurveSpineStrategy(),
        };

        public List<TrackChainAnchor[]> Extract(IReadOnlyCollection<TrackPiece> placed)
            => Extract(placed, 1f);

        // cellSize scales every grid coordinate to world units. Pass terrain.CellSize
        // when chains must align with rendered geometry (which is also scaled by cellSize
        // via the placement service). 1f keeps the legacy 1-unit-per-cell convention.
        public List<TrackChainAnchor[]> Extract(IReadOnlyCollection<TrackPiece> placed, float cellSize)
        {
            var spines = new List<TrackChainAnchor[]>();
            foreach (var p in placed)
            {
                if (!_catalog.TryGet(p.Shape, out var def)) continue;
                var spine = BuildWorldSpine(def, p.Origin, p.Facing, cellSize);
                if (spine != null && spine.Length >= 2)
                    spines.Add(spine);
            }

            int n = spines.Count;
            var adj = new Dictionary<long, List<(int idx, bool isStart)>>(n * 2);
            for (int i = 0; i < n; i++)
            {
                AddAdj(adj, SpatialQuantizer.Key(spines[i][0].WorldPos), (i, true));
                AddAdj(adj, SpatialQuantizer.Key(spines[i][^1].WorldPos), (i, false));
            }

            var visited = new bool[n];
            var chains = new List<TrackChainAnchor[]>();

            // First pass — start from any open end (degree-1) and walk forward.
            for (int i = 0; i < n; i++)
            {
                if (visited[i]) continue;
                long sk = SpatialQuantizer.Key(spines[i][0].WorldPos);
                long ek = SpatialQuantizer.Key(spines[i][^1].WorldPos);
                bool startOpen = adj[sk].Count == 1;
                bool endOpen = adj[ek].Count == 1;
                if (!startOpen && !endOpen) continue;

                bool reverseFirst = !startOpen;
                var chain = WalkChain(i, reverseFirst, spines, adj, visited);
                if (chain.Length >= 2) chains.Add(chain);
            }

            // Second pass — closed loops (every endpoint has degree 2).
            for (int i = 0; i < n; i++)
            {
                if (visited[i]) continue;
                var chain = WalkChain(i, false, spines, adj, visited);
                if (chain.Length >= 2) chains.Add(chain);
            }
            return chains;
        }

        private static TrackChainAnchor[] WalkChain(
            int startIdx, bool reverseFirst,
            List<TrackChainAnchor[]> spines,
            Dictionary<long, List<(int, bool)>> adj,
            bool[] visited)
        {
            var output = new List<TrackChainAnchor>();
            int cur = startIdx;
            bool reverse = reverseFirst;
            int safety = 0;
            while (safety++ < spines.Count + 1)
            {
                if (visited[cur]) break;
                visited[cur] = true;
                AppendSpine(output, spines[cur], reverse);
                Vector3 exit = reverse ? spines[cur][0].WorldPos : spines[cur][^1].WorldPos;
                if (!adj.TryGetValue(SpatialQuantizer.Key(exit), out var attached)) break;

                int next = -1;
                bool nextStart = false;
                foreach (var (nIdx, nIsStart) in attached)
                {
                    if (nIdx == cur || visited[nIdx]) continue;
                    next = nIdx;
                    nextStart = nIsStart;
                    break;
                }
                if (next == -1) break;
                cur = next;
                reverse = !nextStart; // arriving at next.start → traverse forward; at next.end → reverse
            }
            return output.ToArray();
        }

        private static void AppendSpine(List<TrackChainAnchor> dest, TrackChainAnchor[] spine, bool reverse)
        {
            int count = spine.Length;
            for (int s = 0; s < count; s++)
            {
                int idx = reverse ? count - 1 - s : s;
                var a = spine[idx];
                if (reverse) a = new TrackChainAnchor(a.WorldPos, -a.Tangent, a.HalfWidth);
                if (dest.Count > 0 && Vector3.Distance(a.WorldPos, dest[^1].WorldPos) < TrackPieceConstants.PortQuantizeGridSize)
                    continue;
                dest.Add(a);
            }
        }

        public TrackChainAnchor[] BuildWorldSpine(TrackPieceDefinition def, GridPosition origin, TrackDirection facing, float cellSize)
        {
            if (!_samplers.TryGetValue(def.Family, out var sampler)) return null;
            var local = sampler.Sample(def);
            if (local == null || local.Count == 0) return null;

            // Tile centre in world coords. Track piece spawns also apply localScale=cellSize
            // (see TrackPlacementService.SpawnGameObject), so multiplying everything by cellSize
            // here keeps chain anchors aligned with rendered geometry.
            Vector3 anchorWorld = new((origin.X + 0.5f) * cellSize, 0f, (origin.Y + 0.5f) * cellSize);
            Vector3 anchorCenterLocal = new(0.5f, 0f, 0.5f);
            Quaternion yaw = Quaternion.Euler(0f, facing.YawDegrees(), 0f);
            // MirrorX flips canonical X around the tile center (0.5). The mesh builder
            // applies this same mirror to the rendered geometry so a LeftCurve reuses
            // the right-curve mesh strategy — we mirror the spine the same way so the
            // extractor stays in lockstep without a per-shape special case.
            float xSign = def.MirrorX ? -1f : 1f;

            var result = new TrackChainAnchor[local.Count];
            for (int i = 0; i < local.Count; i++)
            {
                var s = local[i];
                Vector3 lp = s.LocalPosition;
                Vector3 lt = s.LocalTangent;
                Vector3 mirroredLp = new((lp.x - 0.5f) * xSign + 0.5f, lp.y, lp.z);
                Vector3 mirroredLt = new(lt.x * xSign, lt.y, lt.z);
                Vector3 wp = anchorWorld + yaw * (mirroredLp - anchorCenterLocal) * cellSize;
                wp.y = 0f;
                Vector3 wt = (yaw * mirroredLt).normalized;
                result[i] = new TrackChainAnchor(wp, wt, s.HalfWidth * cellSize);
            }
            return result;
        }

        private static void AddAdj(Dictionary<long, List<(int, bool)>> adj, long key, (int, bool) entry)
        {
            if (!adj.TryGetValue(key, out var list))
            {
                list = new List<(int, bool)>();
                adj[key] = list;
            }
            list.Add(entry);
        }
    }
}
