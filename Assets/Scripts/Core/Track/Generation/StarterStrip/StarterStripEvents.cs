using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Track.Generation.StarterStrip
{
    /// <summary>
    /// Fired by <see cref="IStarterStripGenerator"/> after it commits a strip.
    /// Carries the start-line pose so the topology service + ghost director can
    /// pin spawn without re-deriving it from open ports.
    /// </summary>
    public readonly record struct StarterStripGeneratedEvent(
        int Octant,
        int PieceCount,
        Vector3 StartLineWorldPos,
        float StartHeading);
}
