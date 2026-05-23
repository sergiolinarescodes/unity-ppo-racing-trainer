using System.Collections.Generic;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.AiDriver.Policy
{
    /// <summary>
    /// Lazy cache over <c>Resources.LoadAll&lt;DriverProfile&gt;("AiDriver/Profiles")</c>.
    /// Falls back to <see cref="DriverProfileSnapshot.Default"/> on miss so scenarios
    /// without authored SOs still produce a valid agent.
    /// </summary>
    internal sealed class DriverProfileRegistry
    {
        private const string ResourcesRoot = "AiDriver/Profiles";

        private Dictionary<string, DriverProfileSnapshot> _byId;

        public DriverProfileSnapshot Get(string profileId)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(profileId)) return DriverProfileSnapshot.Default;
            return _byId.TryGetValue(profileId, out var s) ? s : DriverProfileSnapshot.Default;
        }

        public bool TryGet(string profileId, out DriverProfileSnapshot snapshot)
        {
            EnsureLoaded();
            if (!string.IsNullOrEmpty(profileId) && _byId.TryGetValue(profileId, out snapshot))
                return true;
            snapshot = DriverProfileSnapshot.Default;
            return false;
        }

        private void EnsureLoaded()
        {
            if (_byId != null) return;
            _byId = new Dictionary<string, DriverProfileSnapshot>();
            var profiles = Resources.LoadAll<DriverProfile>(ResourcesRoot);
            for (int i = 0; i < profiles.Length; i++)
            {
                var snap = profiles[i].ToSnapshot();
                _byId[snap.Id] = snap;
            }
        }
    }
}
