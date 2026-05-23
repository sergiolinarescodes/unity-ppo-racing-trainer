// Drag-build core primitives — pure logic, no Unity engine deps beyond UnityEngine.Vector3
// (kept for wire compatibility with the input/preview layers). Discretizer is fully
// deterministic and unit-testable.
using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.Terrain;
using Unidad.Core.Grid;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Track.Authoring.Drag
{
    /// <summary>
    /// Per-tile lattice the drag editor projects the cursor onto. Selected by
    /// <see cref="ITerrainLatticeClassifier"/> from the underlying terrain tile.
    /// </summary>
    public enum LatticeKind : byte
    {
        /// <summary>4-direction lattice (N/E/S/W).</summary>
        Cardinal = 0,
        /// <summary>4-direction lattice (NE/SE/SW/NW).</summary>
        Diagonal = 1,
        /// <summary>Tile cannot be built on (peak/saddle/etc.).</summary>
        Forbidden = 2
    }

    /// <summary>
    /// Adapter that maps a tile (or world position) to its drag-build lattice.
    /// Encapsulates the terrain-as-classifier rule so the drag session is
    /// decoupled from terrain internals.
    /// </summary>
    public interface ITerrainLatticeClassifier
    {
        LatticeKind ClassifyAt(TerrainPosition tile);
        LatticeKind ClassifyAt(Vector3 worldXZ);
    }

    internal sealed class TerrainLatticeClassifier : ITerrainLatticeClassifier
    {
        private readonly ITerrainService _terrain;

        public TerrainLatticeClassifier(ITerrainService terrain)
        {
            _terrain = terrain ?? throw new ArgumentNullException(nameof(terrain));
        }

        public LatticeKind ClassifyAt(TerrainPosition tile)
        {
            if (!_terrain.IsInitialized || !_terrain.IsInBounds(tile)) return LatticeKind.Forbidden;
            var t = _terrain.GetTile(tile);
            return Classify(t.Shape);
        }

        public LatticeKind ClassifyAt(Vector3 worldXZ)
        {
            if (!_terrain.IsInitialized) return LatticeKind.Forbidden;
            if (!_terrain.TryWorldToTile(worldXZ.x, worldXZ.z, out var pos)) return LatticeKind.Forbidden;
            return ClassifyAt(pos);
        }

        public static LatticeKind Classify(TerrainShape shape)
        {
            switch (shape)
            {
                case TerrainShape.Flat:
                case TerrainShape.RampN:
                case TerrainShape.RampE:
                case TerrainShape.RampS:
                case TerrainShape.RampW:
                    return LatticeKind.Cardinal;
                case TerrainShape.DiagonalTile:
                    return LatticeKind.Diagonal;
                default:
                    return LatticeKind.Forbidden;
            }
        }
    }

    /// <summary>
    /// One sampled cursor position during a drag, snapped to a terrain tile and
    /// tagged with the lattice that tile dictates.
    /// </summary>
    public readonly record struct DragWaypoint(TerrainPosition Tile, Vector3 World, LatticeKind Lattice);

    /// <summary>
    /// One concrete catalog placement the discretizer wants the placement service
    /// to commit on drag-release.
    /// </summary>
    public readonly record struct DiscretizedPiece(
        TrackPieceShape Shape,
        GridPosition Origin,
        TrackDirection Facing,
        TrackPieceVariantId Variant);

    /// <summary>
    /// Pure function — turn an ordered drag-waypoint sequence into a chain of
    /// catalog pieces. Auto-injects transition pieces at lattice mismatches
    /// (cardinal↔diagonal port → opposite-lattice drag).
    /// </summary>
    public interface IChainDiscretizer
    {
        IReadOnlyList<DiscretizedPiece> Discretize(
            IReadOnlyList<DragWaypoint> waypoints,
            TrackDirection? anchorOutwardDir);
    }

    /// <summary>
    /// Greedy step-by-step discretizer. For each consecutive waypoint pair:
    /// emit one straight in the step direction. Inject curve pieces at every
    /// (prev_dir, next_dir) change — lookup in the corner table below.
    /// Auto-bridge: if the drag's first step direction's lattice differs from
    /// the anchor port's lattice, prepend the matching transition.
    /// </summary>
    internal sealed class GreedyChainDiscretizer : IChainDiscretizer
    {
        public IReadOnlyList<DiscretizedPiece> Discretize(
            IReadOnlyList<DragWaypoint> waypoints,
            TrackDirection? anchorOutwardDir)
        {
            var result = new List<DiscretizedPiece>();
            if (waypoints == null || waypoints.Count < 2) return result;

            // Compute step directions, deduplicating consecutive identical tiles.
            // For axis-aligned multi-tile gaps (e.g. cursor moved fast and we
            // recorded (0,0) then (3,0)), expand into N single-tile unit steps
            // so the chain emits N consecutive straights instead of dropping
            // the gap. Off-axis gaps (e.g. (0,0)→(3,1)) can't be decomposed
            // into a single direction and are skipped.
            var steps = new List<(TerrainPosition From, TerrainPosition To, TrackDirection Dir)>();
            for (int i = 1; i < waypoints.Count; i++)
            {
                var from = waypoints[i - 1].Tile;
                var to = waypoints[i].Tile;
                int dx = to.X - from.X;
                int dz = to.Z - from.Z;
                if (dx == 0 && dz == 0) continue;
                int adx = dx < 0 ? -dx : dx;
                int adz = dz < 0 ? -dz : dz;
                bool axisAligned = dx == 0 || dz == 0 || adx == adz;
                if (!axisAligned) continue;
                int count = adx > adz ? adx : adz;
                int sx = dx == 0 ? 0 : (dx > 0 ? 1 : -1);
                int sz = dz == 0 ? 0 : (dz > 0 ? 1 : -1);
                if (!TryUnitStepDirection(sx, sz, out var dir)) continue;
                var cursor = from;
                for (int k = 0; k < count; k++)
                {
                    var nextTile = new TerrainPosition(cursor.X + sx, cursor.Z + sz);
                    steps.Add((cursor, nextTile, dir));
                    cursor = nextTile;
                }
            }
            if (steps.Count == 0) return result;

            // Build the per-tile direction table. Each tile in the drag's tile
            // path gets an (in, out) pair: "in" = direction the road enters
            // the tile, "out" = direction it leaves. The first tile's in is
            // the anchor's outward direction (if any). The last tile's out
            // is null (chain terminus). One piece per tile:
            //   in == out (or one is null)  → straight in the non-null direction
            //   in != out                   → corner piece via ResolveCorner
            // This avoids the prior bug where a curve and the following
            // straight both targeted the same tile and silently overlapped.
            int tileCount = steps.Count + 1;
            var tilePath = new (TerrainPosition Tile, TrackDirection? In, TrackDirection? Out)[tileCount];
            tilePath[0] = (waypoints[0].Tile, anchorOutwardDir, null);
            for (int i = 0; i < steps.Count; i++)
            {
                var s = steps[i];
                var head = tilePath[i];
                head.Out = s.Dir;
                tilePath[i] = head;
                tilePath[i + 1] = (s.To, s.Dir, null);
            }

            for (int i = 0; i < tileCount; i++)
            {
                var entry = tilePath[i];
                if (!entry.In.HasValue && !entry.Out.HasValue) continue;

                TrackPieceShape shape;
                TrackDirection facing;
                if (entry.In.HasValue && entry.Out.HasValue && entry.In.Value != entry.Out.Value)
                {
                    var (curve, curveFacing) = ResolveCorner(entry.In.Value, entry.Out.Value);
                    if (!curve.HasValue) continue; // unreachable angle pair — skip
                    shape = curve.Value;
                    facing = curveFacing;
                }
                else
                {
                    var dir = entry.Out ?? entry.In ?? TrackDirection.North;
                    var (straight, straightFacing) = ResolveStraight(dir);
                    shape = straight;
                    facing = straightFacing;
                }
                result.Add(new DiscretizedPiece(
                    shape,
                    new GridPosition(entry.Tile.X, entry.Tile.Z),
                    facing,
                    TrackPieceVariantId.Default));
            }

            return result;
        }

        // ---- direction helpers ----

        private static bool TryUnitStepDirection(int sx, int sz, out TrackDirection dir)
        {
            if ((sx == 0 && sz == 0) || sx < -1 || sx > 1 || sz < -1 || sz > 1)
            { dir = default; return false; }
            dir = (sx, sz) switch
            {
                (0, 1) => TrackDirection.North,
                (1, 1) => TrackDirection.NorthEast,
                (1, 0) => TrackDirection.East,
                (1, -1) => TrackDirection.SouthEast,
                (0, -1) => TrackDirection.South,
                (-1, -1) => TrackDirection.SouthWest,
                (-1, 0) => TrackDirection.West,
                (-1, 1) => TrackDirection.NorthWest,
                _ => TrackDirection.North
            };
            return true;
        }

        // ---- straight resolution ----

        /// <summary>
        /// Map a step direction to (canonical-piece-id, facing). Canonical
        /// straight ports are (S, N) — facing rotates them so one of them
        /// becomes the step direction's outgoing port.
        /// </summary>
        private static (TrackPieceShape shape, TrackDirection facing) ResolveStraight(TrackDirection dir)
        {
            if (dir.IsCardinal())
            {
                // Canonical Straight_1x1 ports: S, N. facing=North gives ports S+N.
                // To make the step go in dir, we need the canonical "north" port
                // (the outgoing end) to face dir. facing = dir (since canonical
                // outgoing is North=0, world outgoing = (0 + facing) mod 8 = facing).
                return (TrackPieceShapes.Straight_1x1, dir);
            }
            // Canonical Straight_Diag_1x1 ports: SW, NE. Outgoing = NE (=1).
            // facing = (dir - NE) mod 8 = dir - 1 mod 8.
            var f = (TrackDirection)((((byte)dir) + 7) & 7);
            return (TrackPieceShapes.Straight_Diag_1x1, f);
        }

        // ---- corner resolution ----

        /// <summary>
        /// Resolve the curve piece + facing inserted between a (prev_dir, next_dir)
        /// pair. Routing is by (handedness, port-pair gap) — completely independent
        /// of which lattices prev/next live on, since the same port-pair gap can
        /// arise from different lattice combos (e.g. 45° card→diag and 45° diag→card
        /// both produce gap-5 pairs). Three families:
        ///   gap 2/6 → Curve_1x1 (right) / LeftCurve_1x1 (left)         — 90° turn
        ///   gap 3/5 → CurveDiagTransition / CurveDiagTransitionLeft    — 45° turn
        ///   gap 1/7 → CurveDiagToCardinal / CurveDiagToCardinalLeft    — 135° turn
        /// Returns null on 0° / 180° (caller splits or skips).
        /// </summary>
        private static (TrackPieceShape? shape, TrackDirection facing) ResolveCorner(TrackDirection prev, TrackDirection next)
        {
            int signedDelta = (((byte)next - (byte)prev) + 8) & 7;
            if (signedDelta == 0 || signedDelta == 4) return (null, TrackDirection.North);

            bool right = signedDelta < 4;
            int gap = (signedDelta - 4 + 8) & 7;

            // When prev is diagonal, the right/left handedness chosen by mesh
            // visuals (Curve_1x1 vs LeftCurve_1x1, Transition vs TransitionLeft)
            // and the handedness needed to land the rotated piece on the
            // INTEGER lattice diverge: the visually-right canonical piece
            // requires an odd (45°/135°/225°/315°) facing, which moves cardinal
            // mid-edge ports off-lattice and creates visible gaps. Swapping to
            // the mirror piece flips the canonical orientation enough that the
            // SAME geometric corner reaches the same outward-port pair via an
            // even (90° / 180° / 270°) facing — preserving lattice alignment AND
            // the visual handedness (the rotated mirror reads as the other
            // handedness from the player's perspective).
            bool useRight = right;
            if (!prev.IsCardinal()) useRight = !useRight;

            TrackPieceShape pick;
            switch (gap)
            {
                case 2:
                case 6:
                    pick = right ? TrackPieceShapes.Curve_1x1 : TrackPieceShapes.LeftCurve_1x1;
                    break;
                case 3:
                case 5:
                    pick = useRight ? TrackPieceShapes.CurveDiagTransition_1x1
                                    : TrackPieceShapes.CurveDiagTransitionLeft_1x1;
                    break;
                case 1:
                case 7:
                    pick = useRight ? TrackPieceShapes.CurveDiagToCardinal_1x1
                                    : TrackPieceShapes.CurveDiagToCardinalLeft_1x1;
                    break;
                default:
                    return (null, TrackDirection.North);
            }

            return FindFacingForPiece(pick, prev.Opposite(), next);
        }

        private static (TrackPieceShape? shape, TrackDirection facing) FindFacingForPiece(
            TrackPieceShape shape, TrackDirection portA, TrackDirection portB)
        {
            var pair = GetCanonicalPair(shape);
            for (byte f = 0; f < 8; f++)
            {
                var rA = (TrackDirection)(((byte)pair.a + f) & 7);
                var rB = (TrackDirection)(((byte)pair.b + f) & 7);
                if ((rA == portA && rB == portB) || (rA == portB && rB == portA))
                    return (shape, (TrackDirection)f);
            }
            return (null, TrackDirection.North);
        }

        /// <summary>
        /// Canonical outward port pair for each curve / transition piece. Mirrors
        /// the entries seeded into <see cref="TrackPieceCatalogSeeder"/>.
        /// </summary>
        private static (TrackDirection a, TrackDirection b) GetCanonicalPair(TrackPieceShape shape)
        {
            if (shape == TrackPieceShapes.Curve_1x1) return (TrackDirection.South, TrackDirection.East);
            if (shape == TrackPieceShapes.LeftCurve_1x1) return (TrackDirection.South, TrackDirection.West);
            if (shape == TrackPieceShapes.CurveDiagTransition_1x1) return (TrackDirection.South, TrackDirection.NorthEast);
            if (shape == TrackPieceShapes.CurveDiagTransitionLeft_1x1) return (TrackDirection.South, TrackDirection.NorthWest);
            if (shape == TrackPieceShapes.CurveDiagToCardinal_1x1) return (TrackDirection.NorthEast, TrackDirection.East);
            if (shape == TrackPieceShapes.CurveDiagToCardinalLeft_1x1) return (TrackDirection.NorthWest, TrackDirection.West);
            if (shape == TrackPieceShapes.CurveDiagHairpin_1x1) return (TrackDirection.NorthEast, TrackDirection.West);
            return (TrackDirection.North, TrackDirection.North);
        }

        /// <summary>
        /// Resolve an AUTO-BRIDGE transition piece inserted at drag start when
        /// the anchor port's outward direction is on a different lattice than
        /// the first dragged step. Same routing as <see cref="ResolveCorner"/> —
        /// the geometry of "from-direction X transitioning to direction Y" is
        /// identical whether X came from an anchor port or from a previous
        /// drag step.
        /// </summary>
        private static (TrackPieceShape? shape, TrackDirection facing) ResolveTransition(
            TrackDirection anchorOutward, TrackDirection stepDir)
        {
            if (anchorOutward.IsCardinal() == stepDir.IsCardinal()) return (null, TrackDirection.North);
            return ResolveCorner(anchorOutward, stepDir);
        }
    }
}
