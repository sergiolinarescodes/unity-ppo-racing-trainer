using UnityPpoRacingTrainer.Core.Track.Catalog.Builders;

namespace UnityPpoRacingTrainer.Core.Track
{
    /// <summary>
    /// Populates the catalog with every canonical piece variant. Canonical = North-facing.
    /// Rotated forms are produced at placement time. Each variant declares ports relative
    /// to the canonical orientation; <c>TrackPlacementService</c> rotates them by facing.
    /// W=2 wide-circuit shapes (Straight_2x1, Straight_2x2, Curve_2x2) were removed —
    /// only single-lane pieces remain. Static kerbs were removed — variants now only
    /// describe wall configurations; kerbs are placed dynamically by the racing-line
    /// kerb service during the ghost-loop preview.
    /// </summary>
    internal static class TrackPieceCatalogSeeder
    {
        public static void Seed(TrackPieceCatalog catalog)
        {
            // ---- Straights (canonical = north-south road) ----
            catalog.Register(MakeStraight(TrackPieceShapes.Straight_1x1, 1, 1));
            catalog.Register(MakeStraight(TrackPieceShapes.Straight_1x2, 1, 2));

            // ---- Curves (canonical = NE quarter-arc, road enters from south, exits east) ----
            // 1×1 small arc centered on the NE corner of the tile, R = 0.5u centerline.
            catalog.Register(new PieceBuilder(TrackPieceShapes.Curve_1x1, TrackPieceFamily.Curve, 1, 1)
                .Road(TrackDirection.South).Road(TrackDirection.East)
                .CurveRadius(TrackPieceConstants.CurveRadiusSmall)
                .AutoBarriers()
                .Variant("OuterWallNear", v =>
                {
                    v.Wall(EdgeAnchor.ArcOuter);
                    v.WallsNear();
                })
                .Variant("OuterWallMid", v =>
                {
                    v.Wall(EdgeAnchor.ArcOuter);
                    v.WallsMid();
                })
                .Variant("Bare", _ => { })
                .Build());

            // 1×2 long-curve: tile (0,0) is straight lead-in (NS), tile (0,1) is the arc that
            // exits east. Ports: south-edge (lane 0) + east-edge at the second tile.
            catalog.Register(new PieceBuilder(TrackPieceShapes.Curve_Long_1x2, TrackPieceFamily.Curve, 1, 2)
                .Road(TrackDirection.South, 0).Road(TrackDirection.East, 0)
                .CurveRadius(TrackPieceConstants.CurveRadiusSmall)
                .Wall(EdgeAnchor.StraightWest, tile: 0)
                .Wall(EdgeAnchor.ArcOuter, tile: 1)
                .Variant("OuterWallNear", v =>
                {
                    v.Wall(EdgeAnchor.StraightWest, tile: 0);
                    v.Wall(EdgeAnchor.ArcOuter, tile: 1);
                    v.WallsNear();
                })
                .Variant("BothWallsMid", v =>
                {
                    v.Wall(EdgeAnchor.StraightWest, tile: 0);
                    v.Wall(EdgeAnchor.StraightEast, tile: 0);
                    v.Wall(EdgeAnchor.ArcOuter, tile: 1);
                    v.WallsMid();
                })
                .Variant("Bare", _ => { })
                .Build());

            // ---- Left curve (canonical = south-enter, west-exit) — mirror of Curve_1x1 ----
            catalog.Register(new PieceBuilder(TrackPieceShapes.LeftCurve_1x1, TrackPieceFamily.Curve, 1, 1)
                .Road(TrackDirection.South).Road(TrackDirection.West)
                .CurveRadius(TrackPieceConstants.CurveRadiusSmall)
                .AutoBarriers()
                .Variant("OuterWallNear", v =>
                {
                    v.Wall(EdgeAnchor.ArcOuter);
                    v.WallsNear();
                })
                .Variant("OuterWallMid", v =>
                {
                    v.Wall(EdgeAnchor.ArcOuter);
                    v.WallsMid();
                })
                .Variant("Bare", _ => { })
                .MirrorX()
                .Build());

            // ---- 45° angled (diagonal) family ----
            // Diagonal pieces are 1×1; ports anchored at tile CORNERS (not mid-edges).

            catalog.Register(new PieceBuilder(TrackPieceShapes.Straight_Diag_1x1, TrackPieceFamily.DiagonalStraight, 1, 1)
                .AllowedTerrain(TerrainShapeMask.Flat | TerrainShapeMask.DiagonalTile)
                .Road(TrackDirection.SouthWest).Road(TrackDirection.NorthEast)
                .Variant("WallLeft", v =>
                {
                    v.Wall(EdgeAnchor.DiagonalLeft);
                    v.WallsNear();
                })
                .Variant("WallRight", v =>
                {
                    v.Wall(EdgeAnchor.DiagonalRight);
                    v.WallsNear();
                })
                .Variant("BothWallsNear", v =>
                {
                    v.Wall(EdgeAnchor.DiagonalLeft);
                    v.Wall(EdgeAnchor.DiagonalRight);
                    v.WallsNear();
                })
                .Variant("BothWallsMid", v =>
                {
                    v.Wall(EdgeAnchor.DiagonalLeft);
                    v.Wall(EdgeAnchor.DiagonalRight);
                    v.WallsMid();
                })
                .Build());

            // Cardinal-to-diagonal transition — south mid-edge → NE corner.
            catalog.Register(new PieceBuilder(TrackPieceShapes.CurveDiagTransition_1x1, TrackPieceFamily.DiagonalCurve, 1, 1)
                .AllowedTerrain(TerrainShapeMask.Flat | TerrainShapeMask.DiagonalTile)
                .Road(TrackDirection.South).Road(TrackDirection.NorthEast)
                .CurveRadius(TrackPieceConstants.CurveRadiusSmall)
                .Variant("BothWallsNear", v =>
                {
                    v.Wall(EdgeAnchor.DiagonalLeft);
                    v.Wall(EdgeAnchor.DiagonalRight);
                    v.WallsNear();
                })
                .Variant("BothWallsMid", v =>
                {
                    v.Wall(EdgeAnchor.DiagonalLeft);
                    v.Wall(EdgeAnchor.DiagonalRight);
                    v.WallsMid();
                })
                .Build());

            // Mirror of the transition — south mid-edge → NW corner.
            catalog.Register(new PieceBuilder(TrackPieceShapes.CurveDiagTransitionLeft_1x1, TrackPieceFamily.DiagonalCurve, 1, 1)
                .AllowedTerrain(TerrainShapeMask.Flat | TerrainShapeMask.DiagonalTile)
                .Road(TrackDirection.South).Road(TrackDirection.NorthWest)
                .CurveRadius(TrackPieceConstants.CurveRadiusSmall)
                .Variant("WallLeft", v =>
                {
                    v.Wall(EdgeAnchor.DiagonalLeft);
                    v.WallsNear();
                })
                .Variant("WallRight", v =>
                {
                    v.Wall(EdgeAnchor.DiagonalRight);
                    v.WallsNear();
                })
                .Variant("BothWallsNear", v =>
                {
                    v.Wall(EdgeAnchor.DiagonalLeft);
                    v.Wall(EdgeAnchor.DiagonalRight);
                    v.WallsNear();
                })
                .Variant("BothWallsMid", v =>
                {
                    v.Wall(EdgeAnchor.DiagonalLeft);
                    v.Wall(EdgeAnchor.DiagonalRight);
                    v.WallsMid();
                })
                .MirrorX()
                .Build());

            // Diagonal-back-to-cardinal "high brake" — NE corner → east mid-edge.
            catalog.Register(new PieceBuilder(TrackPieceShapes.CurveDiagToCardinal_1x1, TrackPieceFamily.DiagonalCurve, 1, 1)
                .AllowedTerrain(TerrainShapeMask.Flat | TerrainShapeMask.DiagonalTile)
                .Road(TrackDirection.NorthEast).Road(TrackDirection.East)
                .CurveRadius(TrackPieceConstants.CurveRadiusSmall)
                .Variant("WallLeft", v =>
                {
                    v.Wall(EdgeAnchor.DiagonalLeft);
                    v.WallsNear();
                })
                .Variant("WallRight", v =>
                {
                    v.Wall(EdgeAnchor.DiagonalRight);
                    v.WallsNear();
                })
                .Variant("BothWallsNear", v =>
                {
                    v.Wall(EdgeAnchor.DiagonalLeft);
                    v.Wall(EdgeAnchor.DiagonalRight);
                    v.WallsNear();
                })
                .Variant("BothWallsMid", v =>
                {
                    v.Wall(EdgeAnchor.DiagonalLeft);
                    v.Wall(EdgeAnchor.DiagonalRight);
                    v.WallsMid();
                })
                .Build());

            // Mirror — NW corner → west mid-edge.
            catalog.Register(new PieceBuilder(TrackPieceShapes.CurveDiagToCardinalLeft_1x1, TrackPieceFamily.DiagonalCurve, 1, 1)
                .AllowedTerrain(TerrainShapeMask.Flat | TerrainShapeMask.DiagonalTile)
                .Road(TrackDirection.NorthWest).Road(TrackDirection.West)
                .CurveRadius(TrackPieceConstants.CurveRadiusSmall)
                .Variant("WallLeft", v =>
                {
                    v.Wall(EdgeAnchor.DiagonalLeft);
                    v.WallsNear();
                })
                .Variant("WallRight", v =>
                {
                    v.Wall(EdgeAnchor.DiagonalRight);
                    v.WallsNear();
                })
                .Variant("BothWallsNear", v =>
                {
                    v.Wall(EdgeAnchor.DiagonalLeft);
                    v.Wall(EdgeAnchor.DiagonalRight);
                    v.WallsNear();
                })
                .Variant("BothWallsMid", v =>
                {
                    v.Wall(EdgeAnchor.DiagonalLeft);
                    v.Wall(EdgeAnchor.DiagonalRight);
                    v.WallsMid();
                })
                .MirrorX()
                .Build());

            // 135° hairpin — NE corner → west mid-edge.
            catalog.Register(new PieceBuilder(TrackPieceShapes.CurveDiagHairpin_1x1, TrackPieceFamily.DiagonalCurve, 1, 1)
                .AllowedTerrain(TerrainShapeMask.Flat | TerrainShapeMask.DiagonalTile)
                .Road(TrackDirection.NorthEast).Road(TrackDirection.West)
                .CurveRadius(TrackPieceConstants.CurveRadiusSmall)
                .Variant("WallLeft", v =>
                {
                    v.Wall(EdgeAnchor.DiagonalLeft);
                    v.WallsNear();
                })
                .Variant("WallRight", v =>
                {
                    v.Wall(EdgeAnchor.DiagonalRight);
                    v.WallsNear();
                })
                .Variant("BothWallsNear", v =>
                {
                    v.Wall(EdgeAnchor.DiagonalLeft);
                    v.Wall(EdgeAnchor.DiagonalRight);
                    v.WallsNear();
                })
                .Variant("BothWallsMid", v =>
                {
                    v.Wall(EdgeAnchor.DiagonalLeft);
                    v.Wall(EdgeAnchor.DiagonalRight);
                    v.WallsMid();
                })
                .Build());

            // ---- Ramp (canonical = rises from south to north) ----
            catalog.Register(new PieceBuilder(TrackPieceShapes.Ramp_1x1, TrackPieceFamily.Ramp, 1, 1)
                .Port(TrackDirection.North, 0, TrackPortState.RoadElevatedHigh)
                .Port(TrackDirection.South, 0, TrackPortState.RoadElevatedLow)
                .AllowedTerrain(TerrainShapeMask.CardinalRamp)
                .Build());
        }

        private static TrackPieceDefinition MakeStraight(TrackPieceShape shape, int width, int length)
        {
            var b = new PieceBuilder(shape, TrackPieceFamily.Straight, width, length)
                .AllowedTerrain(TerrainShapeMask.FlatAndCardinalRamp);
            for (int lane = 0; lane < width; lane++)
            {
                b.Road(TrackDirection.North, lane);
                b.Road(TrackDirection.South, lane);
            }
            b.AutoBarriers();

            // Variants: V-key cycles these at placement time. Index 0 always equals the
            // default geometry above (both walls, walls-near shoulder). "Bare" is the
            // edge-less variant the closure shape system pins via BareVariantOf.
            b.Variant("Bare", _ => { });
            b.Variant("WestWallNear", v =>
            {
                for (int t = 0; t < length; t++) v.Wall(EdgeAnchor.StraightWest, tile: t);
                v.WallsNear();
            });
            b.Variant("EastWallNear", v =>
            {
                for (int t = 0; t < length; t++) v.Wall(EdgeAnchor.StraightEast, tile: t);
                v.WallsNear();
            });
            b.Variant("BothWallsNear", v =>
            {
                for (int t = 0; t < length; t++)
                {
                    v.Wall(EdgeAnchor.StraightWest, tile: t);
                    v.Wall(EdgeAnchor.StraightEast, tile: t);
                }
                v.WallsNear();
            });
            b.Variant("BothWallsMid", v =>
            {
                for (int t = 0; t < length; t++)
                {
                    v.Wall(EdgeAnchor.StraightWest, tile: t);
                    v.Wall(EdgeAnchor.StraightEast, tile: t);
                }
                v.WallsMid();
            });
            b.Variant("WestWallMid", v =>
            {
                for (int t = 0; t < length; t++) v.Wall(EdgeAnchor.StraightWest, tile: t);
                v.WallsMid();
            });
            b.Variant("EastWallMid", v =>
            {
                for (int t = 0; t < length; t++) v.Wall(EdgeAnchor.StraightEast, tile: t);
                v.WallsMid();
            });
            return b.Build();
        }
    }
}
