using UnityPpoRacingTrainer.Core.AiDriver.Physics;
using Unidad.Core.Abstractions;

namespace UnityPpoRacingTrainer.Core.AiDriver.Training
{
    /// <summary>
    /// Combines the base <see cref="EpisodeRunner"/> reward source with the
    /// optional personality-aware <see cref="RewardShaper"/>.
    /// <see cref="PostStep"/> sums both deltas and prefers a non-null end
    /// reason from either side (the shaper wins on tie — fuel-out /
    /// puncture-off-track are terminal and should not be drowned out by a
    /// simultaneous lap-complete). When the shaper is not registered, this
    /// falls through to the base source unchanged.
    /// </summary>
    internal sealed class CompositeEpisodeRewardSource : IEpisodeRewardSource
    {
        private readonly IEpisodeRewardSource _base;
        private readonly IRewardShaper _shaper;
        private readonly ITimeProvider _time;
        private float _lastTime;

        public CompositeEpisodeRewardSource(
            IEpisodeRewardSource baseSource,
            IRewardShaper shaper,
            ITimeProvider time)
        {
            _base = baseSource;
            _shaper = shaper;
            _time = time;
        }

        public void OnEpisodeBegin(CarId carId)
        {
            _base.OnEpisodeBegin(carId);
            _shaper?.OnEpisodeBegin(carId);
            _lastTime = _time?.Time ?? 0f;
        }

        public StepResult PostStep(CarId carId)
        {
            var baseR = _base.PostStep(carId);
            if (_shaper == null) return baseR;

            float now = _time?.Time ?? 0f;
            float dt = UnityEngine.Mathf.Max(0f, now - _lastTime);
            _lastTime = now;

            var perTick = _shaper.AccumulatePerTick(carId, dt);
            var drained = _shaper.Drain(carId);

            float totalReward = baseR.RewardDelta + perTick.RewardDelta + drained.RewardDelta;
            EpisodeEndReason? end = drained.End ?? baseR.End;
            return new StepResult(totalReward, end);
        }

        public void OnAgentUnregistered(CarId carId) => _base.OnAgentUnregistered(carId);
    }
}
