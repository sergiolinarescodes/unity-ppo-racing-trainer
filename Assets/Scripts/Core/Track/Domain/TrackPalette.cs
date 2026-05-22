using UnityPpoRacingTrainer.Core.Terrain;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Track
{
    /// <summary>
    /// Vertex-color palette for procedurally generated track meshes. Defaults map
    /// from <see cref="TerrainPalette.BoneAndSlate"/> so the visual language stays unified
    /// with the terrain showcase.
    /// </summary>
    public readonly record struct TrackPalette(
        Color Road,
        Color RoadEdge,
        Color LaneDivider,
        Color PianoRed,
        Color PianoWhite,
        Color RampStripe,
        Color UnderDeck,
        Color Wall)
    {
        public static TrackPalette Default => FromTerrain(TerrainPalette.BoneAndSlate);

        // Walls render in the RACING signal-red so external/internal barriers
        // are immediately distinguishable from the curb edge (which uses RoadEdge,
        // a much darker neutral). Helps both the human watching training and the
        // visual debug overlays.
        private static readonly Color SignalRed = new Color(0.85f, 0.15f, 0.15f, 1f);

        public static TrackPalette FromTerrain(TerrainPalette p) => new(
            Road: p.SideDark,
            RoadEdge: p.Edge,
            LaneDivider: p.Top,
            PianoRed: p.Accent,
            PianoWhite: p.Top,
            RampStripe: p.Accent2,
            // UnderDeck pinned to the road's gray rather than p.SideLight (near-white).
            // The ribbon overlay's outer fillet (Core/Track/Ribbon/TrackRibbonMeshBuilder.cs:120-144)
            // lerps from RoadEdge → UnderDeck across the apron's cross-section. Any slope
            // anywhere in the chain re-runs the chain-wide Y smoother, lifts the road row,
            // and steepens the cross-section — under the iso camera that exposes a much
            // larger swath of UnderDeck. With UnderDeck == Road the lifted apron stays
            // visually identical to the flat piece's road surface, so a single sloped tile
            // no longer flips the whole network from gray to white.
            UnderDeck: p.SideDark,
            Wall: SignalRed);
    }
}
