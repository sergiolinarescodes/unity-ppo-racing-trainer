namespace UnityPpoRacingTrainer.Core.Terrain.Generators
{
    /// <summary>
    /// Preset terrain generators, ordered roughly from "easy to build on" (mostly flat,
    /// few or no slopes) to "hard to build on" (lots of varied terrain). Most modes are
    /// geometrically constructed to use only Flat and cardinal Ramps; Mountainous and
    /// the plateau-corner cases also produce some Peak/Pit/Saddle tiles.
    /// </summary>
    public enum TerrainGeneratorMode : byte
    {
        Plains,            // fully flat
        GentleSlope,       // single ramp across the whole map (one direction)
        CenterPit,         // flat with a single depression in the middle
        CenterMound,       // flat with a single raised plateau in the middle
        PerimeterRing,     // raised border, flat interior
        TerracedRows,      // stacked Z-strips at varying levels (rice-paddy look)
        Mountainous        // smooth noise, all 14 shape types possible
    }
}
