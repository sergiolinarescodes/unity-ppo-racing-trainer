using UnityPpoRacingTrainer.Core.AiDriver.Physics;
using UnityPpoRacingTrainer.Core.AiDriver.Training.Rewards;
using UnityPpoRacingTrainer.Core.AiDriver.Training.Stages;
using Unidad.Core.Abstractions;

namespace UnityPpoRacingTrainer.Core.AiDriver.Training
{
    /// <summary>
    /// Combines the base <see cref="EpisodeRunner"/> reward source with the
    /// optional personality-aware <see cref="RewardShaper"/> AND any extra
    /// <c>IRewardChannel</c> plug-ins listed in the active version manifest's
    /// <c>rewardChannels[]</c> array. <see cref="PostStep"/> sums all deltas
    /// and prefers a non-null end reason from any source (the shaper / channels
    /// win on tie — fuel-out / puncture-off-track / etc. are terminal and
    /// should not be drowned out by a simultaneous lap-complete).
    ///
    /// Per-tick gating: channels are skipped for stage ids not listed in their
    /// manifest entry's <c>activeInStages</c> array (empty = always active).
    /// The single gating point lives here so a new channel only needs to write
    /// its reward logic; the stage gate is data.
    ///
    /// When the shaper or the channel list is empty, this falls through to the
    /// base source unchanged — the extension surface is dormant by default.
    /// </summary>
    internal sealed class CompositeEpisodeRewardSource : IEpisodeRewardSource
    {
        private readonly IEpisodeRewardSource _base;
        private readonly IRewardShaper _shaper;
        private readonly ITimeProvider _time;
        private readonly ActiveRewardChannels _channels;
        private readonly IActiveStageProfile _activeStage;
        private float _lastTime;

        public CompositeEpisodeRewardSource(
            IEpisodeRewardSource baseSource,
            IRewardShaper shaper,
            ITimeProvider time,
            ActiveRewardChannels channels = null,
            IActiveStageProfile activeStage = null)
        {
            _base = baseSource;
            _shaper = shaper;
            _time = time;
            _channels = channels ?? ActiveRewardChannels.Empty;
            _activeStage = activeStage;
        }

        public void OnEpisodeBegin(CarId carId)
        {
            _base.OnEpisodeBegin(carId);
            _shaper?.OnEpisodeBegin(carId);
            for (int i = 0; i < _channels.Channels.Count; i++)
                _channels.Channels[i].Channel.OnEpisodeBegin(carId);
            _lastTime = _time?.Time ?? 0f;
        }

        public StepResult PostStep(CarId carId)
        {
            var baseR = _base.PostStep(carId);

            float now = _time?.Time ?? 0f;
            float dt = UnityEngine.Mathf.Max(0f, now - _lastTime);
            _lastTime = now;

            float totalReward = baseR.RewardDelta;
            EpisodeEndReason? end = baseR.End;

            if (_shaper != null)
            {
                var perTick = _shaper.AccumulatePerTick(carId, dt);
                var drained = _shaper.Drain(carId);
                totalReward += perTick.RewardDelta + drained.RewardDelta;
                end = drained.End ?? perTick.End ?? end;
            }

            if (_channels.Channels.Count > 0)
            {
                int stageId = _activeStage?.Current?.StageId ?? 0;
                for (int i = 0; i < _channels.Channels.Count; i++)
                {
                    var ac = _channels.Channels[i];
                    if (!ac.IsActiveInStage(stageId)) continue;
                    var perTick = ac.Channel.AccumulatePerTick(carId, dt);
                    var drained = ac.Channel.Drain(carId);
                    totalReward += perTick.RewardDelta + drained.RewardDelta;
                    end = drained.End ?? perTick.End ?? end;
                }
            }

            return new StepResult(totalReward, end);
        }

        public void OnAgentUnregistered(CarId carId)
        {
            _base.OnAgentUnregistered(carId);
            for (int i = 0; i < _channels.Channels.Count; i++)
                _channels.Channels[i].Channel.OnAgentUnregistered(carId);
        }
    }
}
