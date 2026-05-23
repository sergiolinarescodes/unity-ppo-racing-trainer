using System;

namespace UnityPpoRacingTrainer.Core.Track
{
    /// <summary>Unique handle per placed piece instance.</summary>
    public readonly record struct TrackPieceId(Guid Value)
    {
        public static TrackPieceId New() => new(Guid.NewGuid());
        public override string ToString() => Value.ToString("N").Substring(0, 8);
    }
}
