using System;
using System.Collections.Generic;

namespace UnityPpoRacingTrainer.Core.Track.Shape
{
    /// <summary>
    /// Compound track pattern (3–12 cubes) printed on a card. Authored as a sequence of
    /// <see cref="TrackStep"/>s — Forward / TurnLeft / TurnRight — anchored at the
    /// player's cursor. The walker resolves steps into the canonical (north-facing)
    /// piece slots <see cref="Pieces"/> the placement pipeline needs. <see cref="Steps"/>
    /// is the source of truth; <see cref="Pieces"/> is derived once at construction.
    /// </summary>
    public sealed class TrackShape
    {
        public TrackShapeId Id { get; }
        public string Name { get; }
        public IReadOnlyList<TrackStep> Steps { get; }
        public IReadOnlyList<TrackShapePiece> Pieces { get; }

        public TrackShape(TrackShapeId id, string name, IReadOnlyList<TrackStep> steps)
        {
            Id = id;
            Name = name ?? string.Empty;
            Steps = steps ?? Array.Empty<TrackStep>();
            Pieces = TrackShapeWalker.Walk(Steps);
        }

        /// <summary>
        /// Bypasses the walker — used for synthetic shapes (e.g. the mirror variant
        /// produced by <see cref="TrackShapeMirror"/>) whose pieces are derived from
        /// another shape's pieces, not from a Forward/Turn step path.
        /// </summary>
        public TrackShape(TrackShapeId id, string name, IReadOnlyList<TrackShapePiece> pieces)
        {
            Id = id;
            Name = name ?? string.Empty;
            Steps = Array.Empty<TrackStep>();
            Pieces = pieces ?? Array.Empty<TrackShapePiece>();
        }
    }
}
