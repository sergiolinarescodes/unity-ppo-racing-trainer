namespace UnityPpoRacingTrainer.Core.Track
{
    /// <summary>
    /// String-keyed handle for a piece definition in the catalog. String avoids
    /// the explosive enum-growth as variant counts climb, and keeps hot-reload friendly.
    /// </summary>
    public readonly record struct TrackPieceShape(string Id)
    {
        public override string ToString() => Id;
    }
}
