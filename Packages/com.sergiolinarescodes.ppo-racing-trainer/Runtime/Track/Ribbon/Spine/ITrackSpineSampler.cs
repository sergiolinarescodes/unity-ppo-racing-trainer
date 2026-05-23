using System.Collections.Generic;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Track.Ribbon.Spine
{
    /// <summary>
    /// One sample along a piece's canonical (north-facing) spline. Local frame: anchor
    /// tile's SW corner at (0,0,0); the piece spans [0, Width] × [0, Length] on XZ.
    /// </summary>
    public readonly record struct SpineSample(Vector3 LocalPosition, Vector3 LocalTangent, float HalfWidth);

    /// <summary>
    /// Strategy for producing the canonical spine of one piece family. Chain extractor
    /// looks up the strategy by <see cref="Family"/> and asks it to sample the
    /// definition. A strategy returns <c>null</c> when it can't handle the specific
    /// dimensions (e.g. the curve strategy rejects W=2 large arcs). Adding a new
    /// piece family is one new strategy class — no switch edit anywhere.
    /// </summary>
    public interface ITrackSpineSampler
    {
        TrackPieceFamily Family { get; }
        IReadOnlyList<SpineSample> Sample(TrackPieceDefinition def);
    }
}
