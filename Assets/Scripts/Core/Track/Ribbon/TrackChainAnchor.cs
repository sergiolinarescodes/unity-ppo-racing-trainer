using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Track.Ribbon
{
    /// <summary>
    /// One point along the centerline of a chain of connected track pieces. Position
    /// is world-space (XZ on the terrain plane; Y is the local rise authored by the
    /// piece — ramps add rise here, the ribbon mesh builder still drapes onto terrain
    /// on top of this Y). Tangent points from this anchor toward the next anchor
    /// along the chain. HalfWidth carries per-anchor road width so a chain that
    /// passes through pieces of different lane counts can taper.
    /// </summary>
    public readonly record struct TrackChainAnchor(
        Vector3 WorldPos,
        Vector3 Tangent,
        float HalfWidth);
}
