using System;
using System.Collections.Generic;
using System.Linq;
using Reflex.Core;
using Unidad.Core.Bootstrap;
using Unidad.Core.Testing;

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

        public TrainingSettingsSystemInstaller(string activeVersionId)
        {
            _activeVersionId = string.IsNullOrEmpty(activeVersionId) ? "latest" : activeVersionId;
        }

        public void Install(ContainerBuilder builder)
        {
            var captured = _activeVersionId;
            builder.AddSingleton(_ => new TrainingSettingsService(captured),
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
