namespace UnityPpoRacingTrainer.Core.Track
{
    /// <summary>Rejects placements whose footprint overlaps any tile already occupied.</summary>
    internal sealed class OverlapValidator : ITrackPlacementValidator
    {
        public PlacementValidation Validate(PlacementContext ctx)
        {
            if (ctx.Occupancy == null) return PlacementValidation.Valid;

            foreach (var cell in ctx.Definition.Footprint.Tiles(ctx.Origin, ctx.Facing))
            {
                if (ctx.Occupancy.ContainsKey(cell))
                    return PlacementValidation.Invalid($"Tile {cell} already occupied");
            }
            return PlacementValidation.Valid;
        }
    }
}
