using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Terrain
{
    /// <summary>
    /// Pure validation/classification of terrain tile shapes from corner heights.
    /// Accepts every 4-corner pattern with range &le; one StepHeight.
    /// </summary>
    internal static class TerrainShapeRules
    {
        public const float StepHeight = 0.5f;
        public const float Eps = 1e-4f;

        public static bool IsHalfStep(float h)
        {
            var n = h / StepHeight;
            return Mathf.Abs(n - Mathf.Round(n)) < Eps;
        }

        public static int ToLevel(float h) => Mathf.RoundToInt(h / StepHeight);
        public static float ToHeight(int level) => level * StepHeight;

        public static bool TryClassify(in CornerHeights c, out TerrainShape shape, out int baseLevel)
        {
            shape = TerrainShape.Flat;
            baseLevel = 0;

            if (!IsHalfStep(c.NW) || !IsHalfStep(c.NE) || !IsHalfStep(c.SE) || !IsHalfStep(c.SW))
                return false;

            var range = c.Range;
            if (range < Eps)
            {
                shape = TerrainShape.Flat;
                baseLevel = ToLevel(c.NW);
                return true;
            }
            if (Mathf.Abs(range - StepHeight) > Eps)
                return false;

            var lo = c.Min;
            baseLevel = ToLevel(lo);
            bool nwHi = c.NW > lo + Eps;
            bool neHi = c.NE > lo + Eps;
            bool seHi = c.SE > lo + Eps;
            bool swHi = c.SW > lo + Eps;

            int highCount = (nwHi ? 1 : 0) + (neHi ? 1 : 0) + (seHi ? 1 : 0) + (swHi ? 1 : 0);
            switch (highCount)
            {
                case 1:
                    if (nwHi) shape = TerrainShape.PeakNW;
                    else if (neHi) shape = TerrainShape.PeakNE;
                    else if (seHi) shape = TerrainShape.PeakSE;
                    else shape = TerrainShape.PeakSW;
                    return true;
                case 2:
                    if (nwHi && neHi) { shape = TerrainShape.RampN; return true; }
                    if (neHi && seHi) { shape = TerrainShape.RampE; return true; }
                    if (seHi && swHi) { shape = TerrainShape.RampS; return true; }
                    if (swHi && nwHi) { shape = TerrainShape.RampW; return true; }
                    if (nwHi && seHi) { shape = TerrainShape.SaddleNwSe; return true; }
                    if (neHi && swHi) { shape = TerrainShape.SaddleNeSw; return true; }
                    return false;
                case 3:
                    if (!nwHi) shape = TerrainShape.PitNW;
                    else if (!neHi) shape = TerrainShape.PitNE;
                    else if (!seHi) shape = TerrainShape.PitSE;
                    else shape = TerrainShape.PitSW;
                    return true;
            }
            return false;
        }

        public static CornerHeights CornersFor(TerrainShape shape, int baseLevel)
        {
            float lo = ToHeight(baseLevel);
            float hi = lo + StepHeight;
            return shape switch
            {
                TerrainShape.Flat => new CornerHeights(lo, lo, lo, lo),
                TerrainShape.RampN => new CornerHeights(hi, hi, lo, lo),
                TerrainShape.RampE => new CornerHeights(lo, hi, hi, lo),
                TerrainShape.RampS => new CornerHeights(lo, lo, hi, hi),
                TerrainShape.RampW => new CornerHeights(hi, lo, lo, hi),
                TerrainShape.PeakNW => new CornerHeights(hi, lo, lo, lo),
                TerrainShape.PeakNE => new CornerHeights(lo, hi, lo, lo),
                TerrainShape.PeakSE => new CornerHeights(lo, lo, hi, lo),
                TerrainShape.PeakSW => new CornerHeights(lo, lo, lo, hi),
                TerrainShape.PitNW => new CornerHeights(lo, hi, hi, hi),
                TerrainShape.PitNE => new CornerHeights(hi, lo, hi, hi),
                TerrainShape.PitSE => new CornerHeights(hi, hi, lo, hi),
                TerrainShape.PitSW => new CornerHeights(hi, hi, hi, lo),
                TerrainShape.SaddleNwSe => new CornerHeights(hi, lo, hi, lo),
                TerrainShape.SaddleNeSw => new CornerHeights(lo, hi, lo, hi),
                TerrainShape.DiagonalTile => new CornerHeights(lo, lo, lo, lo),
                _ => new CornerHeights(lo, lo, lo, lo)
            };
        }
    }
}
