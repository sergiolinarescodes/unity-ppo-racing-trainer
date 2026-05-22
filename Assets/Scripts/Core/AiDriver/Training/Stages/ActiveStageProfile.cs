using System;
using System.Text;
using UnityPpoRacingTrainer.Core.AiDriver.Versions;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.AiDriver.Training.Stages
{
    /// <summary>
    /// Resolves the currently active <see cref="IStageProfile"/> from
    /// <see cref="IStageIdProvider"/> and exposes a fast
    /// <see cref="Has(StageFeature)"/> check for handler-internal early-returns.
    /// Caches the resolved profile and re-fetches lazily when the stage id
    /// changes, then emits a single-line diagnostic to the console:
    ///
    /// <code>
    /// [StageProfile] stage=1 'Stage1Grid' features={OvertakeReward,...} opponents=11 fuel=Abundant personality=Uniform
    /// </code>
    ///
    /// One log per transition (not per car-episode) so 200-agent stage 0
    /// runs don't spam the console.
    /// </summary>
    public interface IActiveStageProfile
    {
        IStageProfile Current { get; }
        bool Has(StageFeature feature);
    }

    internal sealed class ActiveStageProfile : IActiveStageProfile
    {
        private readonly IStageProfileRegistry _registry;
        private readonly IStageIdProvider _stage;
        private readonly IStageProfile _fallback;

        private IStageProfile _cached;
        private int _cachedStageId = int.MinValue;
        private int _lastLoggedStageId = int.MinValue;

        /// <summary>
        /// Production ctor — pulls the stage-profile registry from the active
        /// <see cref="IAiDriverVersionProfile"/>. Stripped-down snapshots may
        /// return a 1-profile registry; Latest returns the 6-stage curriculum.
        /// </summary>
        public ActiveStageProfile(IAiDriverVersionProfile profile, IStageIdProvider stage)
            : this(profile?.StageProfiles ?? throw new ArgumentNullException(nameof(profile)),
                   stage,
                   PickFallback(profile.StageProfiles)) { }

        /// <summary>
        /// Test ctor — caller provides registry + fallback directly. Used by
        /// the stage-test fixtures and convention tests.
        /// </summary>
        public ActiveStageProfile(IStageProfileRegistry registry, IStageIdProvider stage, IStageProfile fallback)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _stage = stage;
            _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        }

        private static IStageProfile PickFallback(IStageProfileRegistry r)
        {
            // Smallest-keyed entry. For Latest this is Stage0SoloWarmupProfile;
            // stripped-down snapshots can register a single fallback profile.
            int min = int.MaxValue;
            IStageProfile pick = null;
            foreach (var k in r.Keys)
                if (k < min) { min = k; pick = r.Get(k); }
            if (pick == null) throw new InvalidOperationException("StageProfileRegistry is empty.");
            return pick;
        }

        public IStageProfile Current
        {
            get
            {
                int sid = _stage?.Resolve() ?? 0;
                if (sid != _cachedStageId || _cached == null)
                {
                    _cached = _registry.TryGet(sid, out var p) ? p : _fallback;
                    _cachedStageId = sid;
                    LogTransitionOnce(_cached);
                }
                return _cached;
            }
        }

        public bool Has(StageFeature feature) => Current.Has(feature);

        private void LogTransitionOnce(IStageProfile p)
        {
            if (p.StageId == _lastLoggedStageId) return;
            _lastLoggedStageId = p.StageId;

            var sb = new StringBuilder(256);
            sb.Append("[StageProfile] stage=").Append(p.StageId)
              .Append(" '").Append(p.Name).Append('\'')
              .Append(" features={");

            bool first = true;
            foreach (StageFeature f in Enum.GetValues(typeof(StageFeature)))
            {
                if (f == StageFeature.None) continue;
                if ((p.Features & f) != f) continue;
                if (!first) sb.Append(',');
                sb.Append(f);
                first = false;
            }

            sb.Append("} opponents=").Append(p.ExpectedOpponentCount)
              .Append(" fuel=").Append(p.Fuel)
              .Append(" personality=").Append(p.Personality);

            Debug.Log(sb.ToString());
        }
    }
}
