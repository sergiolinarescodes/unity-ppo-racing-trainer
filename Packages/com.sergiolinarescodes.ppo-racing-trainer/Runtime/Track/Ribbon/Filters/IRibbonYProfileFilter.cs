namespace UnityPpoRacingTrainer.Core.Track.Ribbon.Filters
{
    /// <summary>
    /// Stage in the centerline-Y processing pipeline. <paramref name="raw"/> is the
    /// unfiltered terrain sample at each spline point and stays untouched between
    /// stages; <paramref name="working"/> starts as a copy of raw and accumulates
    /// the result. Composable like the placement-validator pipeline — adding a new
    /// stage (banking lift, slope clamp, dip floor) is one new class.
    /// </summary>
    public interface IRibbonYProfileFilter
    {
        void Apply(float[] raw, float[] working);
    }
}
