namespace UnityPpoRacingTrainer.Core.Track.Shape.Heading
{
    /// <summary>
    /// Centralises the bit-arithmetic that aligns diagonal-piece local facings to
    /// a desired world heading. Hard-coded offsets like <c>((byte)h + 5) &amp; 7</c>
    /// are shorthand for "rotate by 5 × 45° = 225° CW" — readable here, opaque
    /// inline in the walker.
    /// </summary>
    public static class DiagonalHeadingHelper
    {
        /// <summary>
        /// Diagonal-Forward straight: the canonical mesh + spine already run SW → NE
        /// (a built-in 45° offset from cardinal). Compensate so transform.rotation
        /// produces the desired diagonal direction. Equivalent to
        /// <see cref="TrackDirectionExtensions.RotateLeft45"/>.
        /// </summary>
        public static TrackDirection DiagonalStraightLocalFacing(TrackDirection worldHeading)
            => worldHeading.RotateLeft45();

        /// <summary>
        /// Diagonal → cardinal RIGHT turn. Uses the LEFT-mirror transition piece
        /// (CurveDiagTransitionLeft_1x1); rotate so its NW exit aligns with the
        /// outgoing cardinal heading.
        /// </summary>
        public static TrackDirection DiagonalToCardinalRightFacing(TrackDirection diagonalHeading)
            => (TrackDirection)(((byte)diagonalHeading + 5) & 7);

        /// <summary>
        /// Diagonal → cardinal LEFT turn. Uses the RIGHT-handed transition piece
        /// (CurveDiagTransition_1x1); rotate by 135° CW so its NE exit aligns with
        /// the outgoing cardinal heading.
        /// </summary>
        public static TrackDirection DiagonalToCardinalLeftFacing(TrackDirection diagonalHeading)
            => (TrackDirection)(((byte)diagonalHeading + 3) & 7);
    }
}
