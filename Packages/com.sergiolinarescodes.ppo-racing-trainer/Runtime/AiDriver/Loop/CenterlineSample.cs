using UnityEngine;

namespace UnityPpoRacingTrainer.Core.AiDriver.Loop
{
    /// <summary>
    /// One lookahead sample along the centerline. Spacing in arc-length is uniform
    /// (set by the caller to <see cref="ITrackQueryService.SampleLookahead"/>) so the
    /// AI sees corner severity at consistent forward distances regardless of how
    /// dense the underlying anchors are.
    /// </summary>
    public readonly record struct CenterlineSample(
        Vector3 Position,
        Vector3 Tangent,
        float HalfWidth,
        float Curvature,
        float Elevation);
}
