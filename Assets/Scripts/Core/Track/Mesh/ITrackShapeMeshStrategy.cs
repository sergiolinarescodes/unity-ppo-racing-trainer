namespace UnityPpoRacingTrainer.Core.Track
{
    /// <summary>
    /// Family-level mesh generator. Each strategy emits canonical (north-facing)
    /// geometry for its family into the shared mesh buffer. The dispatcher
    /// (<see cref="ITrackPieceMeshBuilder"/>) routes by <see cref="TrackPieceFamily"/>.
    /// </summary>
    internal interface ITrackShapeMeshStrategy
    {
        TrackPieceFamily Family { get; }
        void Build(MeshBuffer buf, TrackPieceDefinition def, TrackPalette palette);
    }
}
