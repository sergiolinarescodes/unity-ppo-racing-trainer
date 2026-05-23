using System;
using System.Collections.Generic;
using Unidad.Core.Grid;

namespace UnityPpoRacingTrainer.Core.Track.Shape
{
    /// <summary>
    /// Closure-only transition shapes used by the realistic track generator's
    /// Tier-1 closer to bridge gaps between authored cards. Each is a 1-piece
    /// shape pinned to its <c>Bare</c> variant (no walls, no kerbs) so the
    /// closure pieces never wear the same furniture as body cards. Replaces the
    /// previous runtime cubic-Bezier fallback.
    /// <para>
    /// Ids share the <see cref="IdPrefix"/> so test code and consumers can
    /// recognise closure-only shapes without a schema change. The four
    /// transition kinds the closer needs:
    /// </para>
    /// <list type="bullet">
    ///   <item><c>closure:rect-straight</c> — inline rect-to-rect filler.</item>
    ///   <item><c>closure:rect-to-curve90</c> — rect-to-rect 90° turn.</item>
    ///   <item><c>closure:rect-to-diag</c> — 45° rect-to-diagonal transition.</item>
    ///   <item><c>closure:rect-to-diag-left</c> — left-handed mirror of the above.</item>
    /// </list>
    /// "diagonal-to-rect" and "diagonal-to-45°-curve" reuse the same 45°
    /// transition piece via Tier-1's per-port anchor enumeration — no extra
    /// shape needed.
    /// </summary>
    internal static class ClosureTransitionShapes
    {
        public const string IdPrefix = "closure:";

        public static readonly TrackShapeId RectStraight   = new(IdPrefix + "rect-straight");
        public static readonly TrackShapeId RectToCurve90  = new(IdPrefix + "rect-to-curve90");
        public static readonly TrackShapeId RectToDiag     = new(IdPrefix + "rect-to-diag");
        public static readonly TrackShapeId RectToDiagLeft = new(IdPrefix + "rect-to-diag-left");
        public static readonly TrackShapeId LongStraight   = new(IdPrefix + "long-straight");

        /// <summary>Length range for the long-straight anchor (cells).</summary>
        public const int LongStraightMinCells = 6;
        public const int LongStraightMaxCells = 10;

        /// <summary>
        /// Registers the closure transition shapes into <paramref name="shapeCatalog"/>.
        /// Each piece's variant is forced to "Bare" (no walls/kerbs); pieces lacking a
        /// named Bare variant retain the catalog default (which is itself bare for
        /// diagonal pieces — see TrackMeshTests.CatalogSeeder_DiagonalAndRampStayBare).
        /// <para>
        /// When <paramref name="longStraightCells"/> is in [6, 10], also registers
        /// <see cref="LongStraight"/> as a single bare straight of that many cells.
        /// The realistic generator's anchor-shape finder picks this up so authored-only
        /// scenarios (where the recipe seeder's <c>LONG_STRAIGHT</c> isn't present)
        /// still get a long opening straight.
        /// </para>
        /// Returns the number of shapes registered (skipping ids already present).
        /// </summary>
        public static int Register(
            TrackShapeCatalog shapeCatalog,
            ITrackPieceCatalog pieceCatalog,
            int longStraightCells = 0)
        {
            if (shapeCatalog == null || pieceCatalog == null) return 0;
            int registered = 0;
            registered += TryRegister(shapeCatalog, pieceCatalog,
                RectStraight,   "Closure Straight",      Steps(TrackStep.Forward));
            registered += TryRegister(shapeCatalog, pieceCatalog,
                RectToCurve90,  "Closure 90° Curve",     Steps(TrackStep.TurnRight));
            registered += TryRegister(shapeCatalog, pieceCatalog,
                RectToDiag,     "Closure 45° Curve (R)", Steps(TrackStep.DiagRight));
            registered += TryRegister(shapeCatalog, pieceCatalog,
                RectToDiagLeft, "Closure 45° Curve (L)", Steps(TrackStep.DiagLeft));
            if (longStraightCells > 0)
            {
                int n = Math.Clamp(longStraightCells, LongStraightMinCells, LongStraightMaxCells);
                var steps = new TrackStep[n];
                for (int i = 0; i < n; i++) steps[i] = TrackStep.Forward;
                registered += TryRegister(shapeCatalog, pieceCatalog,
                    LongStraight, $"Closure Long Straight ({n})", steps);
            }
            return registered;
        }

        public static bool IsClosureShapeId(TrackShapeId id) =>
            id.Id != null && id.Id.StartsWith(IdPrefix, StringComparison.Ordinal);

        public static bool IsClosureShapeId(string id) =>
            id != null && id.StartsWith(IdPrefix, StringComparison.Ordinal);

        private static TrackStep[] Steps(params TrackStep[] s) => s;

        private static int TryRegister(
            TrackShapeCatalog shapeCatalog,
            ITrackPieceCatalog pieceCatalog,
            TrackShapeId id,
            string name,
            IReadOnlyList<TrackStep> steps)
        {
            if (shapeCatalog.Has(id)) return 0;
            // Walk the steps via the standard constructor, then rebuild the shape
            // with each piece's variant pinned to Bare so closure placements never
            // emit walls/kerbs.
            var walked = new TrackShape(id, name, steps);
            var bare = new TrackShapePiece[walked.Pieces.Count];
            for (int i = 0; i < walked.Pieces.Count; i++)
            {
                var p = walked.Pieces[i];
                bare[i] = new TrackShapePiece(
                    p.PieceType,
                    p.Offset,
                    p.LocalFacing,
                    BareVariantOf(pieceCatalog, p.PieceType));
            }
            var bareShape = new TrackShape(id, name, bare);
            shapeCatalog.Register(bareShape);
            return 1;
        }

        private static TrackPieceVariantId BareVariantOf(ITrackPieceCatalog catalog, TrackPieceShape shape)
        {
            if (catalog == null || !catalog.TryGet(shape, out var def) || def == null || !def.HasVariants)
                return TrackPieceVariantId.Default;
            for (int i = 0; i < def.Variants.Count; i++)
            {
                if (string.Equals(def.Variants[i].DisplayName, "Bare", StringComparison.OrdinalIgnoreCase))
                    return new TrackPieceVariantId((byte)i);
            }
            return TrackPieceVariantId.Default;
        }
    }

    /// <summary>
    /// Seeds the compound shape catalog with the v1 racing presets. Shapes are now
    /// authored as <see cref="TrackStep"/> sequences — the walker translates each
    /// to the (piece-type, offset, facing) tuples the placement pipeline expects.
    /// Validates each preset against the piece catalog (every walked piece must
    /// exist) and against itself (no two pieces may share a tile under canonical
    /// shape facing). Throws on any malformed preset so boot fails loud.
    /// </summary>
    internal static class TrackShapeCatalogSeeder
    {
        public static void Seed(TrackShapeCatalog shapeCatalog, ITrackPieceCatalog pieceCatalog)
        {
            // F = Forward, R = TurnRight, L = TurnLeft. Read each preset as a path.
            Register(shapeCatalog, pieceCatalog, TrackShapes.LongStraight,  "Long Straight",  Steps('F','F','F','F'));
            Register(shapeCatalog, pieceCatalog, TrackShapes.RightTurn,     "Right Turn",     Steps('F','R','F'));
            Register(shapeCatalog, pieceCatalog, TrackShapes.LeftTurn,      "Left Turn",      Steps('F','L','F'));
            Register(shapeCatalog, pieceCatalog, TrackShapes.SCurve,        "S-Curve",        Steps('F','R','L','F'));
            Register(shapeCatalog, pieceCatalog, TrackShapes.Hairpin,       "Hairpin",        Steps('F','R','R','F'));
            Register(shapeCatalog, pieceCatalog, TrackShapes.Chicane,       "Chicane",        Steps('F','R','F','L','F'));
            Register(shapeCatalog, pieceCatalog, TrackShapes.Zigzag,        "Zigzag",         Steps('R','L','R','L'));
            Register(shapeCatalog, pieceCatalog, TrackShapes.LoopQuarter,   "Loop Quarter",   Steps('F','R','F','R','F'));

            // ---- v2 additions: more variety for placement / magnet testing. ----
            Register(shapeCatalog, pieceCatalog, TrackShapes.SingleStraight, "Single Straight", Steps('F'));
            Register(shapeCatalog, pieceCatalog, TrackShapes.QuickRight,     "Quick Right",     Steps('R','F'));
            Register(shapeCatalog, pieceCatalog, TrackShapes.QuickLeft,      "Quick Left",      Steps('L','F'));
            Register(shapeCatalog, pieceCatalog, TrackShapes.LongRight,      "Long Right",      Steps('F','F','R','F'));
            Register(shapeCatalog, pieceCatalog, TrackShapes.WideS,          "Wide S",          Steps('F','F','R','L','F','F'));
            Register(shapeCatalog, pieceCatalog, TrackShapes.UTurnLong,      "U-Turn Long",     Steps('F','R','R','F','L','L','F'));
            Register(shapeCatalog, pieceCatalog, TrackShapes.ZStepRight,     "Z-Step Right",    Steps('F','R','F','L','F'));
            Register(shapeCatalog, pieceCatalog, TrackShapes.ZStepLeft,      "Z-Step Left",     Steps('F','L','F','R','F'));
            Register(shapeCatalog, pieceCatalog, TrackShapes.Detour,         "Detour",          Steps('F','R','F','L','F','L','F','R','F'));
            Register(shapeCatalog, pieceCatalog, TrackShapes.LongChicane,    "Long Chicane",    Steps('F','F','R','L','F','F','L','R','F','F'));

            // ---- v3 additions: 45° angled compound shapes. ----
            // Lowercase r/l are 45° turns. After a 45° turn the heading is diagonal,
            // so subsequent F-steps lay diagonal straights; another 45° turn brings
            // the heading back to cardinal.
            Register(shapeCatalog, pieceCatalog, TrackShapes.DiagRightTurn,    "Diag Right Turn",    Steps('F','r','F'));
            Register(shapeCatalog, pieceCatalog, TrackShapes.DiagLeftTurn,     "Diag Left Turn",     Steps('F','l','F'));
            Register(shapeCatalog, pieceCatalog, TrackShapes.DiagSidestepRight, "Diag Sidestep Right", Steps('F','r','F','l','F'));
            Register(shapeCatalog, pieceCatalog, TrackShapes.DiagSidestepLeft,  "Diag Sidestep Left",  Steps('F','l','F','r','F'));
            Register(shapeCatalog, pieceCatalog, TrackShapes.DiagSpiral,       "Diag Spiral",        Steps('F','r','F','r','F','r','F','r'));

            // Pure diagonal extensions: 45° transition then N diagonal straights, no
            // closing curve. The magnet picks either boundary port — cardinal entry
            // or diagonal exit — so the same card seeds a new diagonal section from
            // a cardinal port or extends an existing diagonal exit further.
            Register(shapeCatalog, pieceCatalog, TrackShapes.DiagStraightRight,     "Diag Straight Right",      Steps('r','F','F','F'));
            Register(shapeCatalog, pieceCatalog, TrackShapes.DiagStraightLeft,      "Diag Straight Left",       Steps('l','F','F','F'));
            Register(shapeCatalog, pieceCatalog, TrackShapes.LongDiagStraightRight, "Long Diag Straight Right", Steps('r','F','F','F','F','F','F'));
            Register(shapeCatalog, pieceCatalog, TrackShapes.LongDiagStraightLeft,  "Long Diag Straight Left",  Steps('l','F','F','F','F','F','F'));

            // Pull in any user-authored ScriptableObjects under Resources/TrackShapes.
            int loaded = TrackShapeAssetLoader.LoadInto(shapeCatalog);
            if (loaded > 0)
                UnityEngine.Debug.Log($"[TrackShapeCatalog] Loaded {loaded} authored shape asset(s) from Resources/TrackShapes.");
        }

        private static TrackStep[] Steps(params char[] codes)
        {
            var arr = new TrackStep[codes.Length];
            for (int i = 0; i < codes.Length; i++) arr[i] = TrackShapeTextParser.ParseStep(codes[i]);
            return arr;
        }

        private static void Register(
            TrackShapeCatalog catalog,
            ITrackPieceCatalog pieces,
            TrackShapeId id,
            string name,
            IReadOnlyList<TrackStep> steps)
        {
            var shape = new TrackShape(id, name, steps);
            Validate(pieces, shape);
            catalog.Register(shape);
        }

        /// <summary>
        /// Asserts every piece type emitted by the walker exists and no two pieces
        /// share a tile under canonical (north-facing) shape rotation. Catches
        /// authoring errors before they reach the validator pipeline.
        /// </summary>
        private static void Validate(ITrackPieceCatalog pieces, TrackShape shape)
        {
            var anchor = new GridPosition(0, 0);
            var occupied = new HashSet<GridPosition>();

            for (int i = 0; i < shape.Pieces.Count; i++)
            {
                var p = shape.Pieces[i];
                if (!pieces.TryGet(p.PieceType, out var def))
                    throw new InvalidOperationException(
                        $"Shape '{shape.Id}' references unknown piece '{p.PieceType.Id}'.");

                var tileOrigin = p.Offset.Apply(anchor, TrackDirection.North);
                var resolvedFacing = p.ResolveFacing(TrackDirection.North);

                foreach (var t in def.Footprint.Tiles(tileOrigin, resolvedFacing))
                {
                    if (!occupied.Add(t))
                        throw new InvalidOperationException(
                            $"Shape '{shape.Id}' piece #{i} ({p.PieceType.Id}) overlaps tile {t}.");
                }
            }
        }
    }
}
