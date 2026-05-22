using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.AiDriver.Versions.Manifest;

namespace UnityPpoRacingTrainer.Core.AiDriver.Training.Rewards
{
    /// <summary>
    /// A resolved manifest <c>rewardChannels[]</c> entry: the
    /// <see cref="IRewardChannel"/> instance the id resolved to plus the
    /// stage gate it should respect. The composite reward source iterates
    /// these per tick and skips channels whose <see cref="ActiveInStages"/>
    /// excludes the current stage id (empty list = always active).
    /// </summary>
    public sealed class ActiveRewardChannel
    {
        public IRewardChannel Channel { get; }
        public IReadOnlyList<int> ActiveInStages { get; }

        public ActiveRewardChannel(IRewardChannel channel, IReadOnlyList<int> activeInStages)
        {
            Channel = channel;
            ActiveInStages = activeInStages ?? System.Array.Empty<int>();
        }

        public bool IsActiveInStage(int stageId)
        {
            if (ActiveInStages.Count == 0) return true;
            for (int i = 0; i < ActiveInStages.Count; i++)
                if (ActiveInStages[i] == stageId) return true;
            return false;
        }
    }
}
