using System;
using UnityPpoRacingTrainer.Core.AiDriver.Training.Curriculum;
using UnityPpoRacingTrainer.Core.Track;

namespace UnityPpoRacingTrainer.Core.AiDriver.Training.Generation
{
    /// <summary>
    /// Pure helper that chooses the next piece for the procedural generator. Holds
    /// no state; the only randomness comes from the supplied <see cref="System.Random"/>.
    /// Closure logic + placement attempts live in <see cref="ProceduralLoopGenerator"/>;
    /// this file is just the random pick + the curve-direction math. Unit-testable
    /// in isolation.
    ///
    /// Curve-direction math for <c>Curve_1x1</c> (canonical facing North → ports South + East):
    /// <list type="bullet">
    /// <item><b>Right turn</b> (heading H → H.RotateRight()): place with facing F = H. Entry port lands on H.Opposite() side; exit on H.RotateRight() side.</item>
    /// <item><b>Left turn</b>  (heading H → H.RotateLeft()):  place with facing F = H.RotateRight(). Same Curve_1x1 piece — no LeftCurve_1x1 dependency.</item>
    /// </list>
    /// Both turns occupy the cell at <c>pos</c>; the cursor advances by the new heading's <c>Step()</c>.
    /// </summary>
    internal static class LoopBuilder
    {
        public enum PieceKind { Straight, RightCurve, LeftCurve, Ramp }

        public readonly record struct Pick(
            PieceKind Kind,
            TrackPieceShape Shape,
            TrackDirection PlacementFacing,
            TrackDirection NextHeading);

        public static Pick PickPiece(in CurriculumStage stage, TrackDirection currentHeading, Random rng)
        {
            float wS = Math.Max(0f, stage.StraightWeight);
            float wR = Math.Max(0f, stage.RightCurveWeight);
            float wL = Math.Max(0f, stage.LeftCurveWeight);
            float wRamp = Math.Max(0f, stage.RampWeight);
            float total = wS + wR + wL + wRamp;
            if (total <= 0f)
            {
                // Degenerate config — fall back to a straight so the walk still progresses.
                return BuildPick(PieceKind.Straight, currentHeading);
            }

            float roll = (float)rng.NextDouble() * total;
            if ((roll -= wS) < 0f) return BuildPick(PieceKind.Straight, currentHeading);
            if ((roll -= wR) < 0f) return BuildPick(PieceKind.RightCurve, currentHeading);
            if ((roll -= wL) < 0f) return BuildPick(PieceKind.LeftCurve, currentHeading);
            return BuildPick(PieceKind.Ramp, currentHeading);
        }

        public static Pick BuildPick(PieceKind kind, TrackDirection heading) => kind switch
        {
            PieceKind.Straight   => new Pick(kind, TrackPieceShapes.Straight_1x1, heading, heading),
            PieceKind.RightCurve => new Pick(kind, TrackPieceShapes.Curve_1x1, heading, heading.RotateRight()),
            PieceKind.LeftCurve  => new Pick(kind, TrackPieceShapes.Curve_1x1, heading.RotateRight(), heading.RotateLeft()),
            PieceKind.Ramp       => new Pick(kind, TrackPieceShapes.Ramp_1x1, heading, heading),
            _                    => new Pick(PieceKind.Straight, TrackPieceShapes.Straight_1x1, heading, heading),
        };
    }
}
