using System.Collections.Generic;
using System.Linq;
using Reflex.Core;
using Unidad.Core.Bootstrap;
using Unidad.Core.Testing;

namespace UnityPpoRacingTrainer.Core.AiDriver.Versions.Manifest
{
    /// <summary>
    /// Phase 1 (dormant): registers the manifest loader output + empty
    /// strategy registries. Does NOT bind <see cref="IAiDriverVersionProfile"/>
    /// — the existing <c>AiDriverVersionsSystemInstaller</c> still wins.
    /// Future phases populate the registries and switch the active-profile
    /// binding here once parity tests pass.
    ///
    /// Install order: BEFORE <c>AiDriverVersionsSystemInstaller</c> so a
    /// later phase's binding override resolves cleanly. Today the install
    /// order is irrelevant because nothing consumes the registries.
    /// </summary>
    public sealed class VersionManifestSystemInstaller : ISystemInstaller
    {
        public void Install(ContainerBuilder builder)
        {
            // Manifests loaded once at bootstrap. Keyed by version_id string.
            builder.AddSingleton(_ => VersionManifestLoader.LoadAll(),
                typeof(IReadOnlyDictionary<string, VersionManifest>));

            builder.AddSingleton(_ => new RewardChannelRegistry(),
                typeof(RewardChannelRegistry), typeof(IRewardChannelRegistry));
            builder.AddSingleton(_ => new PhysicsModelRegistry(),
                typeof(PhysicsModelRegistry), typeof(IPhysicsModelRegistry));
            builder.AddSingleton(_ => new ObservationWriterRegistry(),
                typeof(ObservationWriterRegistry), typeof(IObservationWriterRegistry));
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
