namespace UnityPpoRacingTrainer.Core.Track.Shape
{
    /// <summary>Well-known shape ids for the built-in racing presets.</summary>
    public static class TrackShapes
    {
        public static readonly TrackShapeId LongStraight = new("LONG_STRAIGHT");
        public static readonly TrackShapeId RightTurn = new("RIGHT_TURN");
        public static readonly TrackShapeId LeftTurn = new("LEFT_TURN");
        public static readonly TrackShapeId SCurve = new("S_CURVE");
        public static readonly TrackShapeId Hairpin = new("HAIRPIN");
        public static readonly TrackShapeId Chicane = new("CHICANE");
        public static readonly TrackShapeId Zigzag = new("ZIGZAG");
        public static readonly TrackShapeId LoopQuarter = new("LOOP_QUARTER");

        // v2 additions — single-piece + extended compound presets for placement
        // and magnet-snap testing. Single-piece shapes let the user drop one tile
        // onto an open port; long compounds exercise multi-piece port chaining.
        public static readonly TrackShapeId SingleStraight = new("SINGLE_STRAIGHT");
        public static readonly TrackShapeId QuickRight = new("QUICK_RIGHT");
        public static readonly TrackShapeId QuickLeft = new("QUICK_LEFT");
        public static readonly TrackShapeId LongRight = new("LONG_RIGHT");
        public static readonly TrackShapeId WideS = new("WIDE_S");
        public static readonly TrackShapeId UTurnLong = new("U_TURN_LONG");
        public static readonly TrackShapeId ZStepRight = new("Z_STEP_RIGHT");
        public static readonly TrackShapeId ZStepLeft = new("Z_STEP_LEFT");
        public static readonly TrackShapeId Detour = new("DETOUR");
        public static readonly TrackShapeId LongChicane = new("LONG_CHICANE");

        // 45° angled (diagonal) compound shapes — each uses the cardinal-to-diagonal
        // transition pieces + diagonal straights to lay out non-orthogonal sections.
        public static readonly TrackShapeId DiagRightTurn = new("DIAG_RIGHT_TURN");
        public static readonly TrackShapeId DiagLeftTurn = new("DIAG_LEFT_TURN");
        public static readonly TrackShapeId DiagSidestepRight = new("DIAG_SIDESTEP_RIGHT");
        public static readonly TrackShapeId DiagSidestepLeft = new("DIAG_SIDESTEP_LEFT");
        public static readonly TrackShapeId DiagSpiral = new("DIAG_SPIRAL");

        // Diagonal extensions — transition then N diagonal straights, no exit curve.
        // Use these to start a diagonal run from a cardinal port or to chain more
        // diagonal travel onto an existing diagonal exit (the magnet anchors on
        // either end of the shape — cardinal entry or diagonal exit).
        public static readonly TrackShapeId DiagStraightRight = new("DIAG_STRAIGHT_RIGHT");
        public static readonly TrackShapeId DiagStraightLeft = new("DIAG_STRAIGHT_LEFT");
        public static readonly TrackShapeId LongDiagStraightRight = new("LONG_DIAG_STRAIGHT_RIGHT");
        public static readonly TrackShapeId LongDiagStraightLeft = new("LONG_DIAG_STRAIGHT_LEFT");
    }
}
