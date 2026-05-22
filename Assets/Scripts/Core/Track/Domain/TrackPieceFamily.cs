namespace UnityPpoRacingTrainer.Core.Track
{
    /// <summary>
    /// Family categorises pieces by their geometric / topological shape, independent
    /// of width / length. Used to dispatch mesh generation strategies and as a coarse
    /// filter for terrain compatibility (e.g. ramp pieces require sloped tiles).
    /// </summary>
    public enum TrackPieceFamily : byte
    {
        Straight,
        Curve,
        Ramp,
        DiagonalStraight,
        DiagonalCurve,
    }
}
