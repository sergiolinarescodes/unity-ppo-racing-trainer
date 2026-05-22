using UnityPpoRacingTrainer.Core.Terrain;

namespace UnityPpoRacingTrainer.Core.Track
{
    /// <summary>
    /// Terrain rules:
    /// <list type="bullet">
    ///   <item><see cref="TerrainShapeCategory.AngleSlope"/> tiles (Peak/Pit/Saddle) always reject —
    ///   no piece can sit cleanly on an angled slope.</item>
    ///   <item><see cref="TerrainShapeCategory.CardinalRamp"/> — only <see cref="TrackPieceFamily.Straight"/>
    ///   pieces (and the matching <see cref="TrackPieceFamily.Ramp"/> piece) are accepted. Curves and
    ///   diagonal pieces declare <see cref="TerrainShapeMask.FlatOnly"/> in the catalog so the mask
    ///   check rejects them here. The matching <see cref="TrackPieceFamily.Ramp"/> piece still
    ///   requires its facing to align with the tile's ramp direction.</item>
    ///   <item><see cref="TerrainShapeCategory.Flat"/> rejects ramp pieces (a ramp piece needs a sloped tile)
    ///   and accepts everything else if the piece's <see cref="TerrainShapeMask"/> permits flat.</item>
    /// </list>
    /// </summary>
    internal sealed class TerrainCompatibilityValidator : ITrackPlacementValidator
    {
        public PlacementValidation Validate(PlacementContext ctx)
        {
            if (ctx.Terrain == null || !ctx.Terrain.IsInitialized)
                return PlacementValidation.Invalid("Terrain not initialized");

            var def = ctx.Definition;

            foreach (var cell in def.Footprint.Tiles(ctx.Origin, ctx.Facing))
            {
                var tp = new TerrainPosition(cell.X, cell.Y);
                if (!ctx.Terrain.IsInBounds(tp))
                    return PlacementValidation.Invalid($"Out of bounds: tile {cell}");

                var tileShape = ctx.Terrain.GetTile(tp).Shape;
                var category = tileShape.GetCategory();

                if (!def.AllowedTerrain.Includes(category))
                    return PlacementValidation.Invalid(
                        $"Piece {def.Shape} disallows {category} terrain at {cell}");

                switch (category)
                {
                    case TerrainShapeCategory.AngleSlope:
                        return PlacementValidation.Invalid(
                            $"Tile {cell} is {tileShape} — angle slopes are unbuildable");

                    case TerrainShapeCategory.Flat:
                        if (def.Family == TrackPieceFamily.Ramp)
                            return PlacementValidation.Invalid(
                                $"Ramp piece requires a sloped tile; {cell} is Flat");
                        break;

                    case TerrainShapeCategory.CardinalRamp:
                        if (def.Family == TrackPieceFamily.Ramp)
                        {
                            if (!ctx.Facing.IsCardinal())
                                return PlacementValidation.Invalid(
                                    $"Ramp piece requires cardinal facing; got {ctx.Facing} at {cell}");
                            var expectedRamp = RampShapeFor(ctx.Facing);
                            if (tileShape != expectedRamp)
                                return PlacementValidation.Invalid(
                                    $"Ramp piece facing {ctx.Facing} expects {expectedRamp} but tile {cell} is {tileShape}");
                        }
                        else if (def.Family != TrackPieceFamily.Straight)
                        {
                            // Curves and diagonal pieces are not allowed on cardinal-ramp tiles —
                            // their geometry doesn't read cleanly when tilted. Only Straight (and
                            // the matching Ramp piece above) drape onto slopes.
                            return PlacementValidation.Invalid(
                                $"Only Straight pieces may sit on cardinal-ramp tiles; {def.Family} disallowed at {cell}");
                        }
                        break;
                }
            }
            return PlacementValidation.Valid;
        }

        private static TerrainShape RampShapeFor(TrackDirection facing) => facing switch
        {
            TrackDirection.North => TerrainShape.RampN,
            TrackDirection.East => TerrainShape.RampE,
            TrackDirection.South => TerrainShape.RampS,
            TrackDirection.West => TerrainShape.RampW,
            _ => TerrainShape.Flat
        };
    }
}
