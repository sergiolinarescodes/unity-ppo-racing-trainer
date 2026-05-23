namespace UnityPpoRacingTrainer.Core.Track
{
    /// <summary>
    /// Builds the procedural mesh for a piece definition in canonical (north-facing)
    /// orientation. Rotation is applied at the GameObject level by the placement service.
    /// Returns both the visible mesh and the parallel canonical-local collision data
    /// (walls + kerbs) — the placement service transforms the latter into world space.
    /// Builders may cache results per (shape, variant) so repeated placements share Mesh assets.
    /// </summary>
    public interface ITrackPieceMeshBuilder
    {
        /// <summary>Build the mesh for the piece's default variant (legacy entry point).</summary>
        MeshBuildResult Build(TrackPieceDefinition def, TrackPalette palette)
            => Build(def, palette, TrackPieceVariantId.Default);

        /// <summary>Build the mesh for a specific named variant of the piece.</summary>
        MeshBuildResult Build(TrackPieceDefinition def, TrackPalette palette, TrackPieceVariantId variantId);
    }
}
