using System.Collections.Generic;

namespace UnityPpoRacingTrainer.Core.AiDriver.Training.Rewards
{
    /// <summary>
    /// Convenience DI wrapper around <c>IReadOnlyList&lt;ActiveRewardChannel&gt;</c>.
    /// Reflex's container resolves concrete types more cleanly than generic
    /// <c>IReadOnlyList</c> bindings; this class is the singleton that holds
    /// the resolved channel list for the active version.
    /// </summary>
    public sealed class ActiveRewardChannels
    {
        public IReadOnlyList<ActiveRewardChannel> Channels { get; }

        public ActiveRewardChannels(IReadOnlyList<ActiveRewardChannel> channels)
        {
            Channels = channels ?? System.Array.Empty<ActiveRewardChannel>();
        }

        public static ActiveRewardChannels Empty { get; } =
            new ActiveRewardChannels(System.Array.Empty<ActiveRewardChannel>());
    }
}
