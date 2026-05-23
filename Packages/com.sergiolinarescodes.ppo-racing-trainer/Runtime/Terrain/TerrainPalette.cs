using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Terrain
{
    /// <summary>
    /// RACING terrain palette. Vertex colors are baked per-face by the mesh builder.
    /// Defaults match the "Bone &amp; Slate" palette from the design bundle.
    /// Accent / Accent2 are used by the category-coloring mode to mark cardinal ramps
    /// and angle slopes respectively.
    /// </summary>
    public readonly record struct TerrainPalette(
        Color Top,
        Color SideLight,
        Color SideDark,
        Color Edge,
        Color Accent,
        Color Accent2)
    {
        public static TerrainPalette BoneAndSlate => new(
            Top: new Color(0.957f, 0.945f, 0.914f),       // #f4f1e9
            SideLight: new Color(0.855f, 0.831f, 0.773f), // #dad4c5
            SideDark: new Color(0.737f, 0.714f, 0.651f),  // #bcb6a6
            Edge: new Color(0.165f, 0.157f, 0.137f),      // #2a2823
            Accent: new Color(0.839f, 0.259f, 0.184f),    // #d6422f signal red
            Accent2: new Color(0.243f, 0.557f, 0.541f));  // #3e8e8a cool teal
    }
}
