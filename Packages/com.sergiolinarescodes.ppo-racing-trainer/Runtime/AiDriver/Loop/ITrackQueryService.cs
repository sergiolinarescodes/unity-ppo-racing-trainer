using System;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.AiDriver.Loop
{
    /// <summary>
    /// The AI driver's "map sense" over the active closed loop. Provides projection
    /// of a world position onto the centerline plus arc-length-uniform lookahead
    /// sampling for observation assembly. Returns default / does nothing when no
    /// loop is closed.
    /// </summary>
    public interface ITrackQueryService
    {
        bool HasLoop { get; }

        /// <summary>
        /// True when EITHER a closed loop is active OR an open ribbon chain
        /// (player still building, ≥2 anchors) is available. Use this when the
        /// caller just needs "is there a drivable centerline to project onto",
        /// regardless of closure status. Projection + lookahead automatically
        /// dispatch to the open-chain path when <see cref="HasLoop"/> is false
        /// but <see cref="HasPath"/> is true.
        /// </summary>
        bool HasPath { get; }

        /// <summary>
        /// Total arc length of the active centerline. Equals the closed loop's
        /// total length when looped; equals the open chain length otherwise.
        /// Returns 0 when neither is available.
        /// </summary>
        float TotalPathLength { get; }

        /// <summary>
        /// Project a world position onto the centerline. Pass <paramref name="hintAnchorIndex"/>
        /// (the previous frame's <see cref="TrackProjection.NearestAnchorIndex"/>) to limit
        /// the search to a ±20 anchor window — O(1) amortized vs O(N) scan when omitted.
        /// </summary>
        TrackProjection Project(Vector3 worldPos, int hintAnchorIndex = -1);

        /// <summary>
        /// Write <paramref name="sampleCount"/> centerline samples into
        /// <paramref name="output"/>, spaced uniformly in arc length over the next
        /// <paramref name="distanceMeters"/> starting one Δs ahead of
        /// <paramref name="startAnchorIndex"/>. Wraps around the loop.
        /// </summary>
        void SampleLookahead(int startAnchorIndex, float distanceMeters, int sampleCount, Span<CenterlineSample> output);

        /// <summary>
        /// Per-sample arc-distance variant of <see cref="SampleLookahead"/>. Each
        /// <paramref name="output"/>[i] is the centerline at
        /// <c>arcAt(startAnchor) + arcOffsetsMeters[i]</c> (mod loop total). Use
        /// for exponentially-spaced lookahead anchors.
        /// </summary>
        void SampleLookaheadAt(int startAnchorIndex, ReadOnlySpan<float> arcOffsetsMeters, Span<CenterlineSample> output);

        /// <summary>
        /// Convenience: returns the centerline elevation at the projection of
        /// <paramref name="worldPos"/>. Used by the car physics to find ground Y.
        /// </summary>
        float GetElevationAt(Vector3 worldPos);
    }
}
