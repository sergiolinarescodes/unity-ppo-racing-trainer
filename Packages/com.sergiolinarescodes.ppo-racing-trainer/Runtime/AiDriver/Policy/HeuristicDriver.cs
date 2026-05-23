using UnityPpoRacingTrainer.Core.AiDriver.Loop;
using UnityPpoRacingTrainer.Core.AiDriver.Physics;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.AiDriver.Policy
{
    /// <summary>
    /// Pure-pursuit driver. Reads one centerline lookahead sample, steers toward it
    /// and reduces throttle when the upcoming arc is sharply curved. Stateless and
    /// deterministic so it can serve as the ML-Agents <c>Heuristic()</c> source AND
    /// as the visual-only smoke driver in the scenario, without two parallel impls.
    /// </summary>
    public static class HeuristicDriver
    {
        public static DriverInput Compute(
            in CarState state,
            ITrackQueryService trackQuery,
            float lookaheadMeters,
            float steerGain,
            float maxThrottle)
        {
            if (trackQuery == null || !trackQuery.HasLoop) return default;

            var proj = trackQuery.Project(state.Position, state.LastAnchorIndex);
            System.Span<CenterlineSample> samples = stackalloc CenterlineSample[1];
            trackQuery.SampleLookahead(proj.NearestAnchorIndex, lookaheadMeters, 1, samples);
            Vector3 target = samples[0].Position;

            float toTargetHeading = Mathf.Atan2(target.x - state.Position.x, target.z - state.Position.z);
            float delta = NormalizeAngle(toTargetHeading - state.Heading);
            float steer = Mathf.Clamp(delta * steerGain, -1f, 1f);

            float curvAbs = Mathf.Abs(samples[0].Curvature);
            float throttle = Mathf.Clamp01(maxThrottle - 0.5f * curvAbs);

            return new DriverInput(steer, throttle, false);
        }

        private static float NormalizeAngle(float a)
        {
            while (a > Mathf.PI) a -= 2f * Mathf.PI;
            while (a < -Mathf.PI) a += 2f * Mathf.PI;
            return a;
        }
    }
}
