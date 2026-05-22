using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Track.Generation.StarterStrip
{
    public readonly record struct StarterStripRequest(
        int Seed,
        int? OctantOverride,
        int MinPieces,
        int MaxPieces);

    public readonly record struct StarterStripResult(
        bool Success,
        int Octant,
        int PieceCount,
        Vector3 StartLineWorldPos,
        float StartHeading);

    /// <summary>
    /// Procedurally lays a 6–10 piece open strip in one of 8 octants on the empty
    /// grid. Used by the main game scene to give the player something to look at
    /// before they place their first authored card. The strip's first piece
    /// embeds the start line; the strip is laid in cardinal+diagonal steps via
    /// <see cref="ITrackPlacementService"/>.
    /// </summary>
    public interface IStarterStripGenerator
    {
        StarterStripResult Generate(in StarterStripRequest request);
    }
}
