using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.Track.Geometry;
using Unidad.Core.Grid;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Track
{
    /// <summary>
    /// One unattached port belonging to a placed piece — the magnet target. World
    /// position is the port's connector point in world space; outward direction is
    /// the unit vector pointing AWAY from the piece (so a neighbour piece's matching
    /// port should face the opposite direction).
    /// </summary>
    public readonly record struct OpenPort(
        TrackPieceId PieceId,
        Vector3 WorldPosition,
        TrackDirection OutwardDirection,
        TrackPortState State);

    /// <summary>
    /// Snapshot helper: walks every placed piece's ports, drops any port already
    /// paired to a neighbour (i.e. another port shares its quantised world XZ),
    /// and exposes a near-mouse query for the magnet snap. Rebuild on every
    /// placement/removal — cheap (linear in the placed-piece count).
    /// </summary>
    public sealed class OpenPortIndex
    {
        private readonly List<OpenPort> _open = new();

        public IReadOnlyList<OpenPort> All => _open;

        public void Rebuild(IReadOnlyCollection<TrackPiece> placed, ITrackPieceCatalog catalog)
            => Rebuild(placed, catalog, 1f);

        // cellSize scales port world positions to match the rendered grid (placement spawn
        // applies localScale = cellSize). Pass terrain.CellSize from the caller so the
        // magnet hit-test runs in true world coords; 1f keeps the legacy 1u-per-cell convention.
        public void Rebuild(IReadOnlyCollection<TrackPiece> placed, ITrackPieceCatalog catalog, float cellSize)
        {
            _open.Clear();
            if (placed == null || placed.Count == 0) return;

            var keyed = new Dictionary<long, List<OpenPort>>(placed.Count * 2);
            foreach (var piece in placed)
            {
                if (!catalog.TryGet(piece.Shape, out var def)) continue;
                foreach (var port in def.Ports)
                {
                    var op = MakeWorldPort(piece, def, port, cellSize);
                    long key = SpatialQuantizer.Key(op.WorldPosition);
                    if (!keyed.TryGetValue(key, out var list))
                    {
                        list = new List<OpenPort>();
                        keyed[key] = list;
                    }
                    list.Add(op);
                }
            }

            foreach (var bucket in keyed.Values)
            {
                if (bucket.Count == 1) _open.Add(bucket[0]);
            }
        }

        public bool TryFindNearest(Vector3 worldXZ, float radius, out OpenPort hit)
        {
            hit = default;
            if (_open.Count == 0) return false;
            float bestSqr = radius * radius;
            int bestIdx = -1;
            for (int i = 0; i < _open.Count; i++)
            {
                Vector3 d = _open[i].WorldPosition - worldXZ;
                d.y = 0f;
                float s = d.sqrMagnitude;
                if (s < bestSqr)
                {
                    bestSqr = s;
                    bestIdx = i;
                }
            }
            if (bestIdx < 0) return false;
            hit = _open[bestIdx];
            return true;
        }

        private static OpenPort MakeWorldPort(TrackPiece piece, TrackPieceDefinition def, TrackPort port, float cellSize)
        {
            var localPos = PortGeometry.CanonicalLocal(def, port);
            float lx = PortGeometry.MirrorXLocal(localPos.x, def.MirrorX);
            float lz = localPos.y;

            var rot = PortGeometry.RotateAroundAnchor(lx, lz, piece.Facing);
            float wx = (piece.Origin.X + 0.5f + rot.x) * cellSize;
            float wz = (piece.Origin.Y + 0.5f + rot.y) * cellSize;

            var outward = PortGeometry.ApplyFacing(port.Side, piece.Facing, def.MirrorX);

            return new OpenPort(
                piece.Id,
                new Vector3(wx, 0f, wz),
                outward,
                port.State);
        }
    }
}
