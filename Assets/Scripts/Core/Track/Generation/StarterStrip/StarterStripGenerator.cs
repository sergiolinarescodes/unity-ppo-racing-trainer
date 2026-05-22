using UnityPpoRacingTrainer.Core.Terrain;
using UnityPpoRacingTrainer.Core.Track.Topology;
using Unidad.Core.EventBus;
using Unidad.Core.Grid;
using Unidad.Core.Systems;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Track.Generation.StarterStrip
{
    internal sealed class StarterStripGenerator : SystemServiceBase, IStarterStripGenerator
    {
        // String-key for a 1x1 straight piece in the canonical seeded catalog.
        // Configurable via constructor so authored-only catalogs can pass a
        // different Id without recompiling.
        private readonly TrackPieceShape _straightShape;

        private readonly ITrackPlacementService _placement;
        private readonly ITrackEndingService _topology;
        private readonly ITerrainService _terrain;

        public StarterStripGenerator(
            IEventBus eventBus,
            ITrackPlacementService placement,
            ITrackEndingService topology,
            ITerrainService terrain,
            TrackPieceShape straightShape) : base(eventBus)
        {
            _placement = placement;
            _topology = topology;
            _terrain = terrain;
            _straightShape = straightShape;
        }

        public StarterStripResult Generate(in StarterStripRequest request)
        {
            var rng = new System.Random(request.Seed);
            // Restrict to the four cardinal octants {N=0, E=2, S=4, W=6}. The
            // canonical Straight_1x1 piece only supports cardinal facings — a
            // diagonal facing rotates the cardinal mesh 45° and produces the
            // visually-wrong "angled rect" the player must never see.
            int octant = request.OctantOverride ?? rng.Next(0, 4) * 2;
            int target = Mathf.Clamp(
                rng.Next(request.MinPieces, request.MaxPieces + 1),
                Mathf.Max(1, request.MinPieces),
                request.MaxPieces);

            var facing = (TrackDirection)((byte)octant & 7);
            var (dx, dz) = facing.Step();

            // Origin: terrain-centre minus half the strip footprint, so the strip
            // is centred inside the bounded terrain rather than around (0,0)
            // (terrain coords are [0..W-1] / [0..D-1] — negative cells are
            // always out-of-bounds and the placement validators would reject).
            int cx = _terrain.IsInitialized ? _terrain.Width / 2 : 0;
            int cz = _terrain.IsInitialized ? _terrain.Depth / 2 : 0;
            var origin = new GridPosition(cx - dx * target / 2, cz - dz * target / 2);

            int placed = 0;
            GridPosition startCell = origin;
            for (int i = 0; i < target; i++)
            {
                var cell = new GridPosition(origin.X + dx * i, origin.Y + dz * i);
                // animate: false — procedural strip lays in instantly so bootstrap
                // doesn't stall waiting on the drop+settle visual.
                var result = _placement.TryPlace(_straightShape, cell, facing, TrackPieceVariantId.Default, animate: false);
                if (!result.Success)
                {
                    Debug.LogWarning($"[StarterStripGenerator] piece {i} rejected at {cell} facing {facing}: {result.Reason}");
                    break;
                }
                if (i == 0) startCell = cell;
                placed++;
            }

            // World pose of the start-line piece. Cells render at world coords
            // scaled by TrackPieceConstants.CellSize (3.0u as of v18d), so the
            // spawn point is (cell + 0.5) * cellSize — anything else lands the
            // ghost outside the rendered strip.
            float cellSize = TrackPieceConstants.CellSize;
            var startPos = new Vector3((startCell.X + 0.5f) * cellSize, 0f, (startCell.Y + 0.5f) * cellSize);
            // Heading: north = 0 rad, then 45° per octant step matching TrackDirection.
            float heading = (float)octant * Mathf.PI * 0.25f;

            if (placed > 0)
            {
                _topology.SetStartLine(startPos, heading);
                Publish(new StarterStripGeneratedEvent(octant, placed, startPos, heading));
                return new StarterStripResult(true, octant, placed, startPos, heading);
            }

            return new StarterStripResult(false, octant, 0, startPos, heading);
        }
    }
}
