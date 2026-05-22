namespace UnityPpoRacingTrainer.Core.Track.Shape
{
    /// <summary>
    /// One Track Piece slot inside a compound <see cref="TrackShape"/>. The piece
    /// type is a catalog key; <see cref="Offset"/> is the shape-local position; and
    /// <see cref="LocalFacing"/> is the piece's facing relative to the shape's facing
    /// (resolved at preview/placement time by composing with the shape's world facing).
    /// <see cref="VariantOverride"/> is null for shapes built from <see cref="TrackStep"/>
    /// recipes (they share the global variant passed to <c>TryPlaceShape</c>); authored
    /// shapes converted from drag-built partials carry per-piece variants here so kerbs
    /// and walls survive the round-trip.
    /// </summary>
    public readonly record struct TrackShapePiece(
        TrackPieceShape PieceType,
        GridOffset Offset,
        TrackDirection LocalFacing,
        TrackPieceVariantId? VariantOverride = null)
    {
        /// <summary>Composes the shape's world facing with this piece's local facing.</summary>
        public TrackDirection ResolveFacing(TrackDirection shapeFacing) =>
            (TrackDirection)(((byte)LocalFacing + (byte)shapeFacing) & 7);
    }
}
