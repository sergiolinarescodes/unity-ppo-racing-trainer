namespace UnityPpoRacingTrainer.Core.Track.Ribbon.Filters
{
    /// <summary>
    /// After smoothing, lifts the entire profile by max(raw - working) so working ≥ raw
    /// everywhere. Guarantees the ribbon never sinks below the underlying terrain
    /// (and therefore never below the per-piece slab tops).
    /// </summary>
    internal sealed class MinClampLiftFilter : IRibbonYProfileFilter
    {
        public void Apply(float[] raw, float[] working)
        {
            int n = raw.Length;
            if (n == 0) return;
            float maxDip = 0f;
            for (int i = 0; i < n; i++)
            {
                float dip = raw[i] - working[i];
                if (dip > maxDip) maxDip = dip;
            }
            if (maxDip <= 0f) return;
            for (int i = 0; i < n; i++) working[i] += maxDip;
        }
    }
}
