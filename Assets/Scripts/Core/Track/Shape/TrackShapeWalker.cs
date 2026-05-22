using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.Track.Geometry;
using UnityPpoRacingTrainer.Core.Track.Shape.Heading;

namespace UnityPpoRacingTrainer.Core.Track.Shape
{
    /// <summary>
    /// Enumerates a shape's <em>boundary</em> ports — the ports that are not paired
    /// with another port of the same shape at the same shape-local position. These
    /// are the ports that face outward into the world after placement and can be
    /// chosen as the magnet anchor. Internal ports (where two pieces of the shape
    /// already kiss) are excluded.
    /// Co-located with <see cref="TrackShapeWalker"/> so Unity's auto-generated
    /// .csproj picks it up without a separate file (which only registers after the
    /// editor refreshes the asmdef).
    /// </summary>
    /// <summary>
    /// Builds the mirror sibling of a shape by mirroring its <see cref="TrackStep"/>
    /// sequence (R↔L, r↔l; F unchanged) and re-walking. The walker correctly
    /// handles the diagonal-heading transitions (which a per-piece X-mirror could
    /// not — DiagonalStraightLocalFacing depends on the heading, not just an X-flip
    /// of the piece's LocalFacing). Co-located in this file so Unity's auto-csproj
    /// picks it up without a new file.
    /// </summary>
    public static class TrackShapeMirror
    {
        /// <summary>
        /// Returns the mirrored shape, or null if the source has no <see cref="TrackShape.Steps"/>
        /// (e.g. synthetic shapes constructed directly from pieces — those have no
        /// canonical mirror).
        /// </summary>
        public static TrackShape Mirror(TrackShape src)
        {
            if (src == null || src.Steps == null || src.Steps.Count == 0) return null;
            var mirroredSteps = new TrackStep[src.Steps.Count];
            for (int i = 0; i < src.Steps.Count; i++)
                mirroredSteps[i] = MirrorStep(src.Steps[i]);
            var id = new TrackShapeId(src.Id.Id + "_MIRROR");
            return new TrackShape(id, src.Name + " (mirrored)", (IReadOnlyList<TrackStep>)mirroredSteps);
        }

        private static TrackStep MirrorStep(TrackStep s) => s switch
        {
            TrackStep.TurnRight => TrackStep.TurnLeft,
            TrackStep.TurnLeft => TrackStep.TurnRight,
            TrackStep.DiagRight => TrackStep.DiagLeft,
            TrackStep.DiagLeft => TrackStep.DiagRight,
            _ => s
        };
    }

    /// <summary>
    /// Builds a "diagonal-entry" variant of a shape by prepending a cardinal↔diagonal
    /// transition piece at the start. The wrapped shape's first boundary port becomes
    /// the transition's diagonal corner — letting the magnet attach to a diagonal
    /// open port without ever rotating the shape facing non-cardinally. Same placement
    /// pipeline (cardinal shape facings, integer-grid offsets, no special-case render)
    /// handles diagonal targets uniformly: diagonal "support" is data-only, expressed
    /// as extra TrackShape variants composed from the same piece catalog the walker
    /// already uses. Co-located with the walker so Unity's auto-csproj picks it up.
    /// </summary>
    public static class TrackShapeDiagonalWrap
    {
        /// <summary>
        /// Returns the wrapped shape, or null if source has no pieces. Transition is
        /// placed with LocalFacing=South so its cardinal port mates with the shifted
        /// original's south entry — every walked shape starts at heading N, so its
        /// first port is on the south face.
        /// </summary>
        public static TrackShape Wrap(TrackShape source, bool rightTransition)
        {
            if (source == null || source.Pieces.Count == 0) return null;

            var transitionType = rightTransition
                ? TrackPieceShapes.CurveDiagTransition_1x1
                : TrackPieceShapes.CurveDiagTransitionLeft_1x1;

            var wrapped = new List<TrackShapePiece>(source.Pieces.Count + 1)
            {
                new TrackShapePiece(transitionType, new GridOffset(0, 0), TrackDirection.South),
            };
            for (int i = 0; i < source.Pieces.Count; i++)
            {
                var orig = source.Pieces[i];
                wrapped.Add(new TrackShapePiece(
                    orig.PieceType,
                    new GridOffset(orig.Offset.Dx, orig.Offset.Dz + 1),
                    orig.LocalFacing));
            }

            var suffix = rightTransition ? "_DIAG_R" : "_DIAG_L";
            return new TrackShape(new TrackShapeId(source.Id.Id + suffix), source.Name, wrapped);
        }
    }

    public static class ShapeBoundaryPorts
    {
        /// <summary>(pieceIndex, portIndex) inside the shape's <c>Pieces</c> /
        /// <c>def.Ports</c> tables.</summary>
        public readonly record struct Entry(int PieceIndex, int PortIndex);

        /// <summary>
        /// Returns the boundary ports in piece-then-port traversal order. Stable
        /// across calls — used by the magnet snap to assign a deterministic index
        /// the user can cycle with R.
        /// </summary>
        public static IReadOnlyList<Entry> Enumerate(TrackShape shape, ITrackPieceCatalog catalog)
        {
            var entries = new List<Entry>();
            if (shape == null || shape.Pieces.Count == 0 || catalog == null) return entries;

            var keys = new List<long>();
            var bucket = new Dictionary<long, int>();
            for (int i = 0; i < shape.Pieces.Count; i++)
            {
                var sp = shape.Pieces[i];
                if (!catalog.TryGet(sp.PieceType, out var def)) continue;
                for (int j = 0; j < def.Ports.Count; j++)
                {
                    var port = def.Ports[j];
                    var local = PortGeometry.CanonicalLocal(def, port);
                    float lx = PortGeometry.MirrorXLocal(local.x, def.MirrorX);
                    float lz = local.y;
                    var rot = PortGeometry.RotateAroundAnchor(lx, lz, sp.LocalFacing);
                    float shx = sp.Offset.Dx + 0.5f + rot.x;
                    float shz = sp.Offset.Dz + 0.5f + rot.y;
                    long key = SpatialQuantizer.Key(shx, shz, TrackPieceConstants.PortQuantizeGridSize);
                    entries.Add(new Entry(i, j));
                    keys.Add(key);
                    bucket.TryGetValue(key, out int c);
                    bucket[key] = c + 1;
                }
            }

            var boundary = new List<Entry>(entries.Count);
            for (int k = 0; k < entries.Count; k++)
            {
                if (bucket[keys[k]] == 1) boundary.Add(entries[k]);
            }
            return boundary;
        }
    }


    /// <summary>
    /// Walks a <see cref="TrackStep"/> sequence and emits the canonical (north-facing)
    /// <see cref="TrackShapePiece"/> list the existing preview/placement pipeline consumes.
    /// Anchor = (0, 0); start heading = north. Each step lays a piece at the current
    /// position with a facing matching the current heading, then advances the head.
    ///
    /// Supports cardinal pieces (F, R/L = 90° turn) and diagonal-transition pieces
    /// (r/l = 45° turn). After a 45° turn the heading is diagonal; subsequent F-steps
    /// place <see cref="TrackPieceShapes.Straight_Diag_1x1"/> tiles. A second 45° turn
    /// brings the heading back to cardinal — the walker picks the right transition
    /// piece + facing to bridge the corner port back to a cardinal mid-edge.
    /// </summary>
    public static class TrackShapeWalker
    {
        public static IReadOnlyList<TrackShapePiece> Walk(IReadOnlyList<TrackStep> steps)
        {
            var pieces = new List<TrackShapePiece>(steps?.Count ?? 0);
            if (steps == null || steps.Count == 0) return pieces;

            int x = 0, z = 0;
            var heading = TrackDirection.North;

            for (int i = 0; i < steps.Count; i++)
            {
                switch (steps[i])
                {
                    case TrackStep.Forward:
                        if (heading.IsDiagonal())
                        {
                            pieces.Add(new TrackShapePiece(
                                TrackPieceShapes.Straight_Diag_1x1,
                                new GridOffset(x, z),
                                DiagonalHeadingHelper.DiagonalStraightLocalFacing(heading)));
                        }
                        else
                        {
                            pieces.Add(new TrackShapePiece(
                                TrackPieceShapes.Straight_1x1, new GridOffset(x, z), heading));
                        }
                        Advance(ref x, ref z, heading);
                        break;

                    case TrackStep.TurnRight:
                        // 90° right (cardinal-only). LeftCurve is the mirrored variant
                        // in the catalog — TurnRight uses the canonical right-curve.
                        if (!heading.IsCardinal()) break;
                        pieces.Add(new TrackShapePiece(
                            TrackPieceShapes.Curve_1x1, new GridOffset(x, z), heading));
                        heading = heading.RotateRight();
                        Advance(ref x, ref z, heading);
                        break;

                    case TrackStep.TurnLeft:
                        if (!heading.IsCardinal()) break;
                        pieces.Add(new TrackShapePiece(
                            TrackPieceShapes.LeftCurve_1x1, new GridOffset(x, z), heading));
                        heading = heading.RotateLeft();
                        Advance(ref x, ref z, heading);
                        break;

                    case TrackStep.DiagRight:
                        EmitDiagTurn(pieces, ref x, ref z, ref heading, rightTurn: true);
                        break;

                    case TrackStep.DiagLeft:
                        EmitDiagTurn(pieces, ref x, ref z, ref heading, rightTurn: false);
                        break;
                }
            }
            return pieces;
        }

        /// <summary>
        /// Lays a 45° transition piece. Two cases:
        /// <list type="bullet">
        ///   <item>Cardinal → diagonal: place the right-handed transition
        ///   (or its left mirror for <paramref name="rightTurn"/> = false) at
        ///   facing = current heading. Heading rotates 45°, advance by the new
        ///   diagonal step.</item>
        ///   <item>Diagonal → cardinal: pick the OPPOSITE-handed transition
        ///   placed at a rotated facing such that its corner port maps to the
        ///   incoming corner and its cardinal port faces the new heading.</item>
        /// </list>
        /// </summary>
        private static void EmitDiagTurn(
            List<TrackShapePiece> pieces, ref int x, ref int z,
            ref TrackDirection heading, bool rightTurn)
        {
            TrackPieceShape shape;
            TrackDirection localFacing;
            if (heading.IsCardinal())
            {
                shape = rightTurn
                    ? TrackPieceShapes.CurveDiagTransition_1x1
                    : TrackPieceShapes.CurveDiagTransitionLeft_1x1;
                localFacing = heading;
            }
            else
            {
                // Diagonal → cardinal needs the opposite-handed piece, rotated so
                // its corner port maps to the incoming diagonal entry and its
                // cardinal port maps to the outgoing cardinal exit.
                if (rightTurn)
                {
                    shape = TrackPieceShapes.CurveDiagTransitionLeft_1x1;
                    localFacing = DiagonalHeadingHelper.DiagonalToCardinalRightFacing(heading);
                }
                else
                {
                    shape = TrackPieceShapes.CurveDiagTransition_1x1;
                    localFacing = DiagonalHeadingHelper.DiagonalToCardinalLeftFacing(heading);
                }
            }

            pieces.Add(new TrackShapePiece(shape, new GridOffset(x, z), localFacing));
            heading = rightTurn ? heading.RotateRight45() : heading.RotateLeft45();
            Advance(ref x, ref z, heading);
        }

        private static void Advance(ref int x, ref int z, TrackDirection heading)
        {
            var (dx, dz) = heading.Step();
            x += dx;
            z += dz;
        }
    }
}
