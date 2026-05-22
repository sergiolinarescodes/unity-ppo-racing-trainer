// Drag-build session — owns the drag-state machine, accumulates waypoints, runs
// the discretizer each cursor tick to produce a ghost preview, commits the
// resulting chain on Commit. State is held as an enum + a few fields rather
// than typed state classes — the lifecycle is short (mouse-down → mouse-up)
// and a typed StateMachine<TContext> would be ceremony for no semantic gain.
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.Terrain;
using Unidad.Core.Grid;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Track.Authoring.Drag
{
    public enum DragSessionState : byte { Idle, Dragging, Committing }

    /// <summary>
    /// Outcome of a drag commit. <see cref="Diagnostic"/> is empty on success;
    /// holds the first piece-rejection reason when partial.
    /// </summary>
    public readonly record struct DragChainPlaceResult(
        int Requested,
        int Committed,
        IReadOnlyList<TrackPieceId> Ids,
        string Diagnostic);

    public interface IDragBuildSession
    {
        DragSessionState State { get; }
        IReadOnlyList<DragWaypoint> Waypoints { get; }
        IReadOnlyList<DiscretizedPiece> PreviewPieces { get; }
        TrackDirection? AnchorOutwardDir { get; }

        bool TryBegin(in OpenPort? anchor, Vector3 worldStart);
        void UpdateCursor(Vector3 worldCursor);
        DragChainPlaceResult Commit();
        void Cancel();
    }

    internal sealed class DragBuildSession : IDragBuildSession
    {
        // Time the cursor must dwell inside a NEW tile before that tile is
        // committed as a waypoint. Lets the user fly through intermediate
        // cells on the way to a real intent tile (e.g. cursor crosses (1,0)
        // briefly while dragging NE toward (1,1) — only (1,1) is recorded).
        // Drag-back retraction is still instant.
        private const float TileDwellSeconds = 0.15f;

        private readonly ITrackPlacementService _placement;
        private readonly IChainDiscretizer _discretizer;
        private readonly ITerrainLatticeClassifier _lattice;
        private readonly ITerrainService _terrain;

        private readonly List<DragWaypoint> _waypoints = new();
        private IReadOnlyList<DiscretizedPiece> _preview = System.Array.Empty<DiscretizedPiece>();
        private TrackDirection? _anchorOutward;
        private DragSessionState _state = DragSessionState.Idle;

        // Dwell state: cursor is over this tile but the tile hasn't been
        // promoted into the waypoint list yet because it hasn't dwelt long
        // enough. Reset whenever the cursor moves to a different tile.
        private TerrainPosition _pendingTile;
        private float _pendingTileEnteredAt;
        private bool _pendingTileSet;

        public DragBuildSession(
            ITrackPlacementService placement,
            IChainDiscretizer discretizer,
            ITerrainLatticeClassifier lattice,
            ITerrainService terrain)
        {
            _placement = placement;
            _discretizer = discretizer;
            _lattice = lattice;
            _terrain = terrain;
        }

        public DragSessionState State => _state;
        public IReadOnlyList<DragWaypoint> Waypoints => _waypoints;
        public IReadOnlyList<DiscretizedPiece> PreviewPieces => _preview;
        public TrackDirection? AnchorOutwardDir => _anchorOutward;

        public bool TryBegin(in OpenPort? anchor, Vector3 worldStart)
        {
            if (_state != DragSessionState.Idle) return false;
            if (!_terrain.IsInitialized) return false;
            if (!_terrain.TryWorldToTile(worldStart.x, worldStart.z, out var startTile)) return false;
            var lat = _lattice.ClassifyAt(startTile);
            if (lat == LatticeKind.Forbidden) return false;

            _waypoints.Clear();
            _waypoints.Add(new DragWaypoint(startTile, worldStart, lat));
            _anchorOutward = anchor.HasValue ? anchor.Value.OutwardDirection : null;
            _preview = System.Array.Empty<DiscretizedPiece>();
            _pendingTileSet = false;
            _state = DragSessionState.Dragging;
            return true;
        }

        public void UpdateCursor(Vector3 worldCursor)
        {
            if (_state != DragSessionState.Dragging) return;
            if (!_terrain.TryWorldToTile(worldCursor.x, worldCursor.z, out var tile)) return;

            var last = _waypoints[_waypoints.Count - 1];

            // Case 1: cursor is still on the most recent waypoint tile. Refresh
            // its stored world pos and clear any dwell candidate.
            if (last.Tile.X == tile.X && last.Tile.Z == tile.Z)
            {
                _waypoints[_waypoints.Count - 1] = new DragWaypoint(last.Tile, worldCursor, last.Lattice);
                _pendingTileSet = false;
                _preview = _discretizer.Discretize(_waypoints, _anchorOutward);
                return;
            }

            // Case 2: cursor is on a tile that's already in the trail (not the
            // tip). Treat as drag-back — instantly retract to that tile.
            if (TryFindWaypointIndex(tile, out int existingIdx))
            {
                _waypoints.RemoveRange(existingIdx + 1, _waypoints.Count - existingIdx - 1);
                _waypoints[existingIdx] = new DragWaypoint(_waypoints[existingIdx].Tile, worldCursor, _waypoints[existingIdx].Lattice);
                _pendingTileSet = false;
                _preview = _discretizer.Discretize(_waypoints, _anchorOutward);
                return;
            }

            // Case 3: cursor is on a NEW tile. Require dwell before promoting
            // it to a real waypoint — fast cursor flicks through intermediate
            // tiles get ignored so the user's intent direction (cardinal vs
            // diagonal) is respected.
            float now = Time.unscaledTime;
            if (!_pendingTileSet || _pendingTile.X != tile.X || _pendingTile.Z != tile.Z)
            {
                _pendingTile = tile;
                _pendingTileEnteredAt = now;
                _pendingTileSet = true;
                return;
            }
            if (now - _pendingTileEnteredAt < TileDwellSeconds) return;

            var lat = _lattice.ClassifyAt(tile);
            if (lat == LatticeKind.Forbidden) { _pendingTileSet = false; return; }
            _waypoints.Add(new DragWaypoint(tile, worldCursor, lat));
            _pendingTileSet = false;
            _preview = _discretizer.Discretize(_waypoints, _anchorOutward);
        }

        private bool TryFindWaypointIndex(in TerrainPosition tile, out int index)
        {
            // Linear scan — drag waypoint counts are bounded by the drag length
            // in cells (typically <50), so O(n) is fine and avoids a hash set
            // alloc on every cursor tick.
            for (int i = _waypoints.Count - 1; i >= 0; i--)
            {
                var w = _waypoints[i].Tile;
                if (w.X == tile.X && w.Z == tile.Z) { index = i; return true; }
            }
            index = -1;
            return false;
        }

        public DragChainPlaceResult Commit()
        {
            if (_state != DragSessionState.Dragging) return new DragChainPlaceResult(0, 0, System.Array.Empty<TrackPieceId>(), "not dragging");
            _state = DragSessionState.Committing;
            var pieces = _preview;
            var placed = new List<TrackPieceId>(pieces.Count);
            string diag = string.Empty;
            for (int i = 0; i < pieces.Count; i++)
            {
                var dp = pieces[i];
                var r = _placement.TryPlace(dp.Shape, dp.Origin, dp.Facing, dp.Variant);
                if (!r.Success)
                {
                    if (string.IsNullOrEmpty(diag)) diag = $"piece#{i} {dp.Shape.Id}@{dp.Origin}: {r.Reason}";
                    continue;
                }
                placed.Add(r.Id);
            }
            _state = DragSessionState.Idle;
            _waypoints.Clear();
            _preview = System.Array.Empty<DiscretizedPiece>();
            _anchorOutward = null;
            return new DragChainPlaceResult(pieces.Count, placed.Count, placed, diag);
        }

        public void Cancel()
        {
            if (_state == DragSessionState.Idle) return;
            _state = DragSessionState.Idle;
            _waypoints.Clear();
            _preview = System.Array.Empty<DiscretizedPiece>();
            _anchorOutward = null;
        }
    }
}
