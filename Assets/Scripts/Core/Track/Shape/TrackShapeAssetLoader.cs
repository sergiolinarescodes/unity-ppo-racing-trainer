using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Track.Shape
{
    /// <summary>
    /// Auto-discovers <see cref="TrackShapeAsset"/>s placed under any
    /// <c>Resources/TrackShapes/</c> folder and registers them into the shape
    /// catalog. Lets designers add shapes without touching code — drop an asset
    /// in, restart play, the new pattern shows up alongside the built-in presets.
    /// Already-registered ids are skipped (built-ins win when names collide).
    /// </summary>
    internal static class TrackShapeAssetLoader
    {
        public const string ResourcesPath = "TrackShapes";

        public static int LoadInto(TrackShapeCatalog catalog)
        {
            var assets = Resources.LoadAll<TrackShapeAsset>(ResourcesPath);
            int registered = 0;
            foreach (var asset in assets)
            {
                if (asset == null) continue;
                if (catalog.Has(asset.Id)) continue;
                catalog.Register(asset.ToTrackShape());
                registered++;
            }
            return registered;
        }
    }
}
