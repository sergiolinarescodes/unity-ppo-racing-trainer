using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Track.Ribbon.Filters
{
    /// <summary>
    /// Symmetric moving-average smoother. Bilinear terrain has slope discontinuities
    /// at tile boundaries — without this pass the road shows a hard angle exactly
    /// where two tile shapes meet.
    /// </summary>
    internal sealed class MovingAverageFilter : IRibbonYProfileFilter
    {
        private readonly int _kernel;

        public MovingAverageFilter(int kernel)
        {
            _kernel = kernel;
        }

        public void Apply(float[] raw, float[] working)
        {
            int n = raw.Length;
            if (n == 0 || _kernel < 2) return;
            int half = _kernel / 2;
            for (int i = 0; i < n; i++)
            {
                float sum = 0f;
                int cnt = 0;
                int lo = Mathf.Max(0, i - half);
                int hi = Mathf.Min(n - 1, i + half);
                for (int k = lo; k <= hi; k++) { sum += raw[k]; cnt++; }
                working[i] = sum / cnt;
            }
        }
    }
}
