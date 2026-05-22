namespace UnityPpoRacingTrainer.Core.Terrain
{
    /// <summary>
    /// All 14 legal terrain tile shapes — every 4-corner config with height range
    /// at most one StepHeight (0.5u). The mesh builder uses bilinear interpolation
    /// across the 4 corners, so all shapes render without special-casing.
    ///
    /// Convention: corners ordered NW (=(x, z+1)), NE (=(x+1, z+1)), SE (=(x+1, z)), SW (=(x, z)).
    /// "High" means baseLevel + 1 step. "Low" means baseLevel.
    ///
    /// Cardinal Ramps (2 adjacent corners high):
    ///   RampN: NW + NE high.
    ///   RampE: NE + SE high.
    ///   RampS: SE + SW high.
    ///   RampW: SW + NW high.
    /// Peaks (1 corner high — tilts a single triangle of the tile up):
    ///   PeakNW, PeakNE, PeakSE, PeakSW.
    /// Pits (3 corners high, 1 low — single triangle dips down):
    ///   PitNW, PitNE, PitSE, PitSW.
    /// Saddles (2 diagonal corners high):
    ///   SaddleNwSe: NW + SE high.
    ///   SaddleNeSw: NE + SW high.
    /// DiagonalTile (height-flat, paint-flagged): all 4 corners equal AND the
    ///   tile carries the diagonal-paint flag — signals that the drag-build
    ///   editor should snap road direction to the 45° lattice (NE/SE/SW/NW)
    ///   instead of cardinal (N/E/S/W). Mesh / SampleHeight treat it as Flat;
    ///   only the lattice classifier sees it as Diagonal.
    /// </summary>
    public enum TerrainShape : byte
    {
        Flat = 0,
        RampN, RampE, RampS, RampW,
        PeakNW, PeakNE, PeakSE, PeakSW,
        PitNW, PitNE, PitSE, PitSW,
        SaddleNwSe, SaddleNeSw,
        DiagonalTile
    }
}
