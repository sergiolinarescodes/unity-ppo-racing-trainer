using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Terrain
{
    /// <summary>
    /// The four corner heights of a single tile, ordered by world axes.
    /// NW = (x, z+1), NE = (x+1, z+1), SE = (x+1, z), SW = (x, z).
    /// </summary>
    public readonly record struct CornerHeights(float NW, float NE, float SE, float SW)
    {
        public float Min => Mathf.Min(Mathf.Min(NW, NE), Mathf.Min(SE, SW));
        public float Max => Mathf.Max(Mathf.Max(NW, NE), Mathf.Max(SE, SW));
        public float Range => Max - Min;
        public bool AllEqual => Mathf.Approximately(NW, NE) && Mathf.Approximately(NE, SE) && Mathf.Approximately(SE, SW);
    }
}
