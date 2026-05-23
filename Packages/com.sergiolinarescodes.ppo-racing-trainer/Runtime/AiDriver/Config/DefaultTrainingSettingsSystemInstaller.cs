using System;
using System.Collections.Generic;
using System.Linq;
using Reflex.Core;
using Unidad.Core.Bootstrap;
using Unidad.Core.Testing;

namespace UnityPpoRacingTrainer.Core.AiDriver.Config
{
    /// <summary>
    /// Consumer-side installer that binds <see cref="ITrainingSettingsService"/>
    /// to a default-valued <see cref="StaticTrainingSettingsService"/>. Use
    /// this in projects that do not have an
    /// <c>Assets/_Bootstrap/Configs/Versions/</c> manifest tree (e.g. a game
    /// pulling this package as a UPM dep).
    ///
    /// Trainer projects use <see cref="TrainingSettingsSystemInstaller"/>
    /// instead — that one projects an actual on-disk version manifest into
    /// the settings shape. Picking exactly one of the two is the consumer's
    /// responsibility; Reflex doesn't allow conditional binding.
    /// </summary>
    public sealed class DefaultTrainingSettingsSystemInstaller : ISystemInstaller
    {
        public void Install(ContainerBuilder builder)
        {
            builder.AddSingleton(_ => new StaticTrainingSettingsService(),
                typeof(StaticTrainingSettingsService), typeof(ITrainingSettingsService));
        }

        public ISystemTestFactory CreateTestFactory() => new DefaultTrainingSettingsTestFactory();
    }

    internal sealed class DefaultTrainingSettingsTestFactory : ISystemTestFactory
    {
        public Type[] TestedServices => new[] { typeof(ITrainingSettingsService) };
        public object CreateForTesting(TestDependencies deps) => null;
        public IEnumerable<ITestScenario> GetScenarios() => Enumerable.Empty<ITestScenario>();
    }
}
