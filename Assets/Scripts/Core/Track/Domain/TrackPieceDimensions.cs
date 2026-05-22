namespace UnityPpoRacingTrainer.Core.Track
{
    /// <summary>
    /// Lane-count and tile-length of a piece in canonical (north-facing) orientation.
    /// Width = number of parallel lanes (1 = single, 2 = double-wide).
    /// Length = number of tiles along the piece's primary axis (1 or 2).
    /// </summary>
    public readonly record struct TrackPieceDimensions(int Width, int Length)
    {
        public int TileCount => Width * Length;
        public override string ToString() => $"{Width}x{Length}";
    }
}
