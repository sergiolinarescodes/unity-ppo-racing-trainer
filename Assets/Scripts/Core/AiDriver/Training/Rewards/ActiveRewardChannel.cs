using UnityPpoRacingTrainer.Core.AiDriver.Versions.Manifest;

namespace UnityPpoRacingTrainer.Core.AiDriver.Training.Rewards
{
    /// <summary>
    /// A resolved manifest <c>rewardChannels[]</c> entry: the
    /// <see cref="IRewardChannel"/> instance the id resolved to. The composite
    /// reward source iterates these per tick.
    /// </summary>
    public sealed class ActiveRewardChannel
    {
        public IRewardChannel Channel { get; }

        public ActiveRewardChannel(IRewardChannel channel)
        {
            Channel = channel;
        }
    }
}
