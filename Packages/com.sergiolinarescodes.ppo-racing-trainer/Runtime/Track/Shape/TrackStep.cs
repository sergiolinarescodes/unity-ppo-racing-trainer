namespace UnityPpoRacingTrainer.Core.Track.Shape
{
    /// <summary>
    /// One move along a Track Shape's path. Each step extends the path from the
    /// last piece's exit — racing tracks are linear, so authoring is just a
    /// sequence of "what comes next". The walker translates a step list into the
    /// concrete <see cref="TrackShapePiece"/> tuples (piece-type / offset / facing)
    /// the placement pipeline already understands.
    /// </summary>
    public enum TrackStep : byte
    {
        /// <summary>One straight tile in the current heading (alias: <c>F</c>). When
        /// the heading is diagonal the walker places a <c>Straight_Diag_1x1</c> instead
        /// of the cardinal <c>Straight_1x1</c>.</summary>
        Forward = 0,

        /// <summary>90° right turn: a cardinal curve piece (alias: <c>R</c>). Only valid
        /// from a cardinal heading.</summary>
        TurnRight = 1,

        /// <summary>90° left turn: a cardinal curve piece (alias: <c>L</c>). Only valid
        /// from a cardinal heading.</summary>
        TurnLeft = 2,

        /// <summary>45° right turn: a diagonal-transition piece (alias: <c>r</c>).
        /// Cardinal → diagonal places a right-handed transition; diagonal → cardinal
        /// places a left-handed transition rotated to bridge the corner back to a
        /// cardinal mid-edge.</summary>
        DiagRight = 3,

        /// <summary>45° left turn: a diagonal-transition piece (alias: <c>l</c>).
        /// Mirror of <see cref="DiagRight"/>.</summary>
        DiagLeft = 4,
    }
}
