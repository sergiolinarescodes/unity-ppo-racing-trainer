using UnityPpoRacingTrainer.Core.Track.Geometry;
using UnityPpoRacingTrainer.Core.Track.Shape;
using Unidad.Core.Grid;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Track
{
    /// <summary>
    /// Computes the (origin, facing) the active shape needs so that an <em>anchor</em>
    /// port lands exactly on a target <see cref="OpenPort"/>. The anchor is identified
    /// by <c>(pieceIndex, portIndex)</c> inside the shape so callers can cycle through
    /// boundary ports (R-key while snapped) instead of being locked to the lead piece's
    /// entry. Convenience overloads default to the lead piece's entry port — canonical
    /// side opposite to the lead piece's local facing — matching the walker's "road
    /// comes in here" semantics.
    /// </summary>
    public static class MagnetSnapResolver
    {
        /// <summary>
        /// Origin solve at an explicit anchor port. <paramref name="shapeFacing"/> is
        /// caller-supplied; for the auto-rotated form use <see cref="TryResolveAlignedAt"/>.
        /// </summary>
        public static bool TryResolveAt(
            TrackShape shape,
            TrackDirection shapeFacing,
            int anchorPieceIndex,
            int anchorPortIndex,
            OpenPort target,
            ITrackPieceCatalog catalog,
            out GridPosition origin)
            => TryResolveAt(shape, shapeFacing, anchorPieceIndex, anchorPortIndex, target, catalog, 1f, out origin);

        // cellSize: the world-units-per-cell scale used to build the OpenPortIndex.
        // target.WorldPosition lives in world coords; divide back to cell coords before
        // the integer-grid round so the resolved origin lands on the correct tile.
        public static bool TryResolveAt(
            TrackShape shape,
            TrackDirection shapeFacing,
            int anchorPieceIndex,
            int anchorPortIndex,
            OpenPort target,
            ITrackPieceCatalog catalog,
            float cellSize,
            out GridPosition origin)
        {
            origin = default;
            if (!TryGetAnchor(shape, anchorPieceIndex, anchorPortIndex, catalog,
                              out var anchorPiece, out var def, out var anchorPort)) return false;

            // Anchor port → piece-local (canonical, then MirrorX-flipped on X).
            Vector2 localInPiece = PortGeometry.CanonicalLocal(def, anchorPort);
            float lx = PortGeometry.MirrorXLocal(localInPiece.x, def.MirrorX);
            float lz = localInPiece.y;

            // Piece-local → shape-local.
            var pieceRot = PortGeometry.RotateAroundAnchor(lx, lz, anchorPiece.LocalFacing);
            float shx = anchorPiece.Offset.Dx + 0.5f + pieceRot.x;
            float shz = anchorPiece.Offset.Dz + 0.5f + pieceRot.y;

            // Shape-local → world via shape facing rotation around shape origin centre.
            var shapeRot = PortGeometry.RotateAroundAnchor(shx, shz, shapeFacing);

            float invC = cellSize > 0f ? 1f / cellSize : 1f;
            float wx = target.WorldPosition.x * invC - 0.5f - shapeRot.x;
            float wz = target.WorldPosition.z * invC - 0.5f - shapeRot.y;
            origin = new GridPosition(Mathf.RoundToInt(wx), Mathf.RoundToInt(wz));
            return true;
        }

        /// <summary>
        /// Solves the unique shape facing that mates the <em>anchor</em> port (selected
        /// by index) to the target — anchor's world outward direction equals
        /// <c>target.OutwardDirection.Opposite()</c> — and the corresponding origin.
        /// </summary>
        public static bool TryResolveAlignedAt(
            TrackShape shape,
            int anchorPieceIndex,
            int anchorPortIndex,
            OpenPort target,
            ITrackPieceCatalog catalog,
            out GridPosition origin,
            out TrackDirection alignedFacing)
            => TryResolveAlignedAt(shape, anchorPieceIndex, anchorPortIndex, target, catalog, 1f, out origin, out alignedFacing);

        public static bool TryResolveAlignedAt(
            TrackShape shape,
            int anchorPieceIndex,
            int anchorPortIndex,
            OpenPort target,
            ITrackPieceCatalog catalog,
            float cellSize,
            out GridPosition origin,
            out TrackDirection alignedFacing)
        {
            origin = default;
            alignedFacing = TrackDirection.North;
            if (!TryGetAnchor(shape, anchorPieceIndex, anchorPortIndex, catalog,
                              out var anchorPiece, out var def, out var anchorPort)) return false;

            // Anchor world side = (LocalFacing + shapeFacing) applied to anchorPort.Side.
            // Want: anchorWorldSide == target.OutwardDirection.Opposite().
            // → shapeFacing = (Opposite − anchorPort.Side − LocalFacing) mod 8.
            // (MirrorX is mesh-only; see PortGeometry.ApplyFacing.)
            int desired = ((int)target.OutwardDirection + 4) & 7;
            int f = (desired - (int)anchorPort.Side - (int)anchorPiece.LocalFacing) & 7;
            alignedFacing = (TrackDirection)f;

            return TryResolveAt(shape, alignedFacing, anchorPieceIndex, anchorPortIndex, target, catalog, cellSize, out origin);
        }

        // ---- Convenience wrappers (lead piece + entry port) ----

        /// <summary>
        /// Origin solve using the lead piece's entry port (canonical side opposite
        /// the lead piece's <see cref="TrackShapePiece.LocalFacing"/>).
        /// </summary>
        public static bool TryResolve(
            TrackShape shape,
            TrackDirection shapeFacing,
            OpenPort target,
            ITrackPieceCatalog catalog,
            out GridPosition origin)
        {
            origin = default;
            if (!TryGetLeadEntry(shape, catalog, out int pieceIdx, out int portIdx)) return false;
            return TryResolveAt(shape, shapeFacing, pieceIdx, portIdx, target, catalog, out origin);
        }

        /// <summary>Aligned solve using the lead piece's entry port.</summary>
        public static bool TryResolveAligned(
            TrackShape shape,
            OpenPort target,
            ITrackPieceCatalog catalog,
            out GridPosition origin,
            out TrackDirection alignedFacing)
        {
            origin = default;
            alignedFacing = TrackDirection.North;
            if (!TryGetLeadEntry(shape, catalog, out int pieceIdx, out int portIdx)) return false;
            return TryResolveAlignedAt(shape, pieceIdx, portIdx, target, catalog, out origin, out alignedFacing);
        }

        // ---- Helpers ----

        private static bool TryGetAnchor(
            TrackShape shape, int pieceIndex, int portIndex, ITrackPieceCatalog catalog,
            out TrackShapePiece piece, out TrackPieceDefinition def, out TrackPort port)
        {
            piece = default;
            def = null;
            port = default;
            if (shape == null || pieceIndex < 0 || pieceIndex >= shape.Pieces.Count) return false;
            piece = shape.Pieces[pieceIndex];
            if (!catalog.TryGet(piece.PieceType, out def)) return false;
            if (portIndex < 0 || portIndex >= def.Ports.Count) return false;
            port = def.Ports[portIndex];
            return true;
        }

        private static bool TryGetLeadEntry(
            TrackShape shape, ITrackPieceCatalog catalog,
            out int pieceIndex, out int portIndex)
        {
            pieceIndex = 0;
            portIndex = -1;
            if (shape == null || shape.Pieces.Count == 0) return false;
            var lead = shape.Pieces[0];
            if (!catalog.TryGet(lead.PieceType, out var def)) return false;
            if (def.Ports.Count == 0) return false;
            // Entry side = canonical side opposite the piece's local heading.
            var entrySide = lead.LocalFacing.Opposite();
            for (int i = 0; i < def.Ports.Count; i++)
            {
                if (def.Ports[i].Side == entrySide) { portIndex = i; return true; }
            }
            // Fallback: first port.
            portIndex = 0;
            return true;
        }
    }
}
