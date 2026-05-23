using UnityPpoRacingTrainer.Core.Track;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.AiDriver.Loop
{
    /// <summary>
    /// Result of projecting a world position onto the active closed loop's centerline.
    /// Heading error is not included — callers that need it derive it from
    /// <see cref="Tangent"/> and the car's own heading, which keeps this struct
    /// usable from non-driver contexts (debug overlays, betting heuristics).
    /// <see cref="Surface"/> reports asphalt vs kerb at the projected point;
    /// <see cref="IsOffTrack"/> stays the off-track gate (centerline distance > halfWidth+tol).
    /// </summary>
    public readonly record struct TrackProjection(
        int NearestAnchorIndex,
        float ArcLengthAlong,
        Vector3 ProjectedPoint,
        Vector3 Tangent,
        float HalfWidth,
        float SignedLateralOffset,
        float ElevationAtPoint,
        bool IsOffTrack,
        SurfaceKind Surface = SurfaceKind.Asphalt);
}
