using System.Collections.Generic;

namespace UnityPpoRacingTrainer.Core.Track.Shape
{
    /// <summary>
    /// Registry of canonical compound shape patterns. Reads dominate (catalog is read
    /// every frame by the cycle service, every preview by the magnet resolver), but
    /// runtime registration matters too: authored cards converted from saved partial
    /// tracks need to land in the same catalog the seeder fills.
    /// </summary>
    public interface ITrackShapeCatalog
    {
        IReadOnlyList<TrackShape> All { get; }
        TrackShape Get(TrackShapeId id);
        bool TryGet(TrackShapeId id, out TrackShape shape);
        bool Has(TrackShapeId id);
        int Count { get; }

        /// <summary>
        /// Add a runtime-built shape to the catalog. Used by
        /// <c>AuthoredCardLoader.LoadAllInto</c> to register cards converted from
        /// saved partial tracks; the seeder uses the same path for built-in recipes.
        /// </summary>
        void Register(TrackShape shape);
    }
}
