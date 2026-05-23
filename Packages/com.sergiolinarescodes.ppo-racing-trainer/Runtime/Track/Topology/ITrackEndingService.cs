using System.Collections.Generic;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Track.Topology
{
    /// <summary>
    /// Resolves the current circuit's open ends and start-line pose. Deduplicates
    /// raw <see cref="TrackPiecePlacedEvent"/> / <see cref="TrackPieceRemovedEvent"/>
    /// into a single <see cref="TrackTopologyChangedEvent"/> that fires only when
    /// the open-end set or closure status actually changes.
    /// </summary>
    public interface ITrackEndingService
    {
        IReadOnlyList<OpenPort> OpenEnds { get; }
        bool IsClosedLoop { get; }

        bool TryGetStartLine(out Vector3 worldPos, out float heading);

        void SetStartLine(Vector3 worldPos, float heading);
    }
}
