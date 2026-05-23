using UnityPpoRacingTrainer.Core.Terrain;

namespace UnityPpoRacingTrainer.Core.Track
{
    /// <summary>Rejects placements whose footprint extends outside the terrain.</summary>
    internal sealed class BoundsValidator : ITrackPlacementValidator
    {
        public PlacementValidation Validate(PlacementContext ctx)
        {
            if (ctx.Terrain == null || !ctx.Terrain.IsInitialized)
                return PlacementValidation.Invalid("Terrain not initialized");

            foreach (var cell in ctx.Definition.Footprint.Tiles(ctx.Origin, ctx.Facing))
            {
                var tp = new TerrainPosition(cell.X, cell.Y);
                if (!ctx.Terrain.IsInBounds(tp))
                    return PlacementValidation.Invalid($"Out of bounds: tile {cell}");
            }
            return PlacementValidation.Valid;
        }
    }
}
