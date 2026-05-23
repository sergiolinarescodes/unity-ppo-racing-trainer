using System;
using System.Collections.Generic;
using System.Linq;
using Reflex.Core;
using Unidad.Core.Bootstrap;
using Unidad.Core.Testing;
using UnityPpoRacingTrainer.Core.AiDriver.Versions.Manifest;

namespace UnityPpoRacingTrainer.Core.AiDriver.Config
{
    /// <summary>
    /// Registers <see cref="ITrainingSettingsService"/>. Add to
    /// <c>TrainerBootstrap.RegisterInstallers</c> FIRST so every downstream
    /// service that injects settings sees a populated value.
    ///
    /// The <c>activeVersionId</c> picks which <c>&lt;id&gt;.json</c> manifest
    /// under <c>Assets/_Bootstrap/Configs/Versions/</c> is projected into
    /// <see cref="TrainingSettings"/>. Same string TrainerBootstrap passes to
    /// <c>AiDriverVersionsSystemInstaller</c> — keeps reward shaper / tire /
    /// physics consts in sync with the resolved version profile.
    /// </summary>
    public sealed class TrainingSettingsSystemInstaller : ISystemInstaller
    {
        private readonly string _activeVersionId;
        private readonly IReadOnlyDictionary<string, VersionManifest> _manifests;

        public TrainingSettingsSystemInstaller(
            string activeVersionId,
            IReadOnlyDictionary<string, VersionManifest> manifests)
        {
            _activeVersionId = string.IsNullOrEmpty(activeVersionId) ? "latest" : activeVersionId;
            _manifests = manifests ?? throw new ArgumentNullException(nameof(manifests));
        }

        public void Install(ContainerBuilder builder)
        {
            var capturedId = _activeVersionId;
            var capturedManifests = _manifests;
            builder.AddSingleton(_ => new TrainingSettingsService(capturedId, capturedManifests),
                typeof(TrainingSettingsService), typeof(ITrainingSettingsService));
        }

        public ISystemTestFactory CreateTestFactory() => new TrainingSettingsTestFactory();
    }

    internal sealed class TrainingSettingsTestFactory : ISystemTestFactory
    {
        public Type[] TestedServices => new[] { typeof(ITrainingSettingsService) };
        public object CreateForTesting(TestDependencies deps) => null;
        public IEnumerable<ITestScenario> GetScenarios() => Enumerable.Empty<ITestScenario>();
    }
}
