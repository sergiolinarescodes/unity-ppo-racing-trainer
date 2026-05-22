namespace UnityPpoRacingTrainer.Core.Track.Shape
{
    /// <summary>String-keyed handle for a compound shape definition in the catalog.</summary>
    public readonly record struct TrackShapeId(string Id)
    {
        public override string ToString() => Id;
    }
}
