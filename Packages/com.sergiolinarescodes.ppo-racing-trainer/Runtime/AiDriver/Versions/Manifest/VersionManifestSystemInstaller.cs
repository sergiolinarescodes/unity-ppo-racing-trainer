using System.Collections.Generic;
using System.Linq;
using UnityPpoRacingTrainer.Core.AiDriver.Policy.Observation;
using Reflex.Core;
using Unidad.Core.Bootstrap;
using Unidad.Core.Testing;

namespace UnityPpoRacingTrainer.Core.AiDriver.Versions.Manifest
{
    /// <summary>
    /// Registers the manifest loader output, the strategy registries, and the
    /// built-in strategy implementations (canonical observation writer, etc).
    /// External code adds new strategies by registering more entries in these
    /// registries from its own ISystemInstaller.
    ///
    /// Install order: BEFORE <c>AiDriverVersionsSystemInstaller</c> so the
    /// active version profile (which is resolved later) can look up its
    /// strategy ids in the registries seeded here.
    /// </summary>
    public sealed class VersionManifestSystemInstaller : ISystemInstaller
    {
        private readonly IReadOnlyDictionary<string, VersionManifest> _manifests;

        public VersionManifestSystemInstaller(IReadOnlyDictionary<string, VersionManifest> manifests)
        {
            _manifests = manifests ?? throw new System.ArgumentNullException(nameof(manifests));
        }

        // Convenience overload for consumers (e.g. the game's GameBootstrap)
        // that don't pre-load the manifest dict elsewhere — kicks off the
        // disk + Resources fallback in VersionManifestLoader.LoadAll.
        public VersionManifestSystemInstaller() : this(VersionManifestLoader.LoadAll()) { }

        public void Install(ContainerBuilder builder)
        {
            // Manifests loaded once at bootstrap; TrainerBootstrap passes the
            // dict in so this installer + TrainingSettingsService + the
            // requiresSideSystems decision all share one disk read.
            var captured = _manifests;
            builder.AddSingleton(_ => captured,
                typeof(IReadOnlyDictionary<string, VersionManifest>));

            // Reward channel registry — empty by default. New IRewardChannel
            // implementations register themselves from their own ISystemInstaller
            // (called after this one).
            builder.AddSingleton(_ => new RewardChannelRegistry(),
                typeof(RewardChannelRegistry), typeof(IRewardChannelRegistry));

            builder.AddSingleton(_ => new PhysicsModelRegistry(),
                typeof(PhysicsModelRegistry), typeof(IPhysicsModelRegistry));

            // Observation writer registry — seeded with the canonical RacingV1
            // writer. Adding a new layout = implement IObservationWriter,
            // register it here from another installer under a new id.
            builder.AddSingleton(_ =>
            {
                var reg = new ObservationWriterRegistry();
                reg.Register(RacingV1ObservationWriter.Instance.Id, RacingV1ObservationWriter.Instance);
                return reg;
            }, typeof(ObservationWriterRegistry), typeof(IObservationWriterRegistry));
        }

        public ISystemTestFactory CreateTestFactory() => new VersionManifestTestFactory();
    }

    internal sealed class VersionManifestTestFactory : ISystemTestFactory
    {
        public System.Type[] TestedServices => new[]
        {
            typeof(IReadOnlyDictionary<string, VersionManifest>),
            typeof(IRewardChannelRegistry),
            typeof(IPhysicsModelRegistry),
            typeof(IObservationWriterRegistry),
        };

        public object CreateForTesting(TestDependencies deps) => null;

        public IEnumerable<ITestScenario> GetScenarios() => Enumerable.Empty<ITestScenario>();
    }
}
