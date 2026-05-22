using System.Collections.Generic;

namespace UnityPpoRacingTrainer.Core.Track
{
    /// <summary>
    /// Read-only registry of every canonical piece definition. Variants for the four
    /// cardinal facings are produced at placement time by rotating the canonical
    /// definition's mesh and ports around the anchor tile — they are not stored here.
    /// </summary>
    public interface ITrackPieceCatalog
    {
        IReadOnlyList<TrackPieceDefinition> All { get; }
        TrackPieceDefinition Get(TrackPieceShape shape);
        bool TryGet(TrackPieceShape shape, out TrackPieceDefinition def);
        bool Has(TrackPieceShape shape);
        int Count { get; }
    }

    /// <summary>
    /// Well-known shape ids. Centralised so callers don't sprinkle string literals.
    /// </summary>
    public static class TrackPieceShapes
    {
        public static readonly TrackPieceShape Straight_1x1 = new("STRAIGHT_1x1");
        public static readonly TrackPieceShape Straight_1x2 = new("STRAIGHT_1x2");
        public static readonly TrackPieceShape Curve_1x1 = new("CURVE_1x1");
        public static readonly TrackPieceShape LeftCurve_1x1 = new("LCURVE_1x1");
        public static readonly TrackPieceShape Curve_Long_1x2 = new("CURVE_LONG_1x2");
        public static readonly TrackPieceShape Ramp_1x1 = new("RAMP_1x1");

        // ---- 45° angled (diagonal) family ----
        // Diagonal straight runs corner-to-corner across the tile (SW ↔ NE in canonical).
        // Transition pieces bridge a cardinal port (south mid-edge) to a diagonal port
        // (NE corner). The "Left" variant is MirrorX'd around the tile center.
        public static readonly TrackPieceShape Straight_Diag_1x1 = new("STRAIGHT_DIAG_1x1");
        public static readonly TrackPieceShape CurveDiagTransition_1x1 = new("CURVE_DIAG_TRANS_1x1");
        public static readonly TrackPieceShape CurveDiagTransitionLeft_1x1 = new("LCURVE_DIAG_TRANS_1x1");

        // Diagonal-back-to-cardinal "high brake" — enters from the NE corner along
        // the diagonal, bends 45° right and exits east mid-edge. Mirror exists for
        // the NW→W flow.
        public static readonly TrackPieceShape CurveDiagToCardinal_1x1 = new("CURVE_DIAG_TO_CARD_1x1");
        public static readonly TrackPieceShape CurveDiagToCardinalLeft_1x1 = new("LCURVE_DIAG_TO_CARD_1x1");

        // 135° hairpin — enters NE corner along the diagonal, sweeps back to exit
        // west mid-edge. Useful for tight loops that need to fold back without a
        // second cardinal-to-diagonal transition piece in between.
        public static readonly TrackPieceShape CurveDiagHairpin_1x1 = new("CURVE_DIAG_HAIRPIN_1x1");
    }
}
