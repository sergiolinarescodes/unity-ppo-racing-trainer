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
    /// </summary>
    public sealed class TrainingSettingsSystemInstaller : ISystemInstaller
    {
        public void Install(ContainerBuilder builder)
        {
            builder.AddSingleton(typeof(TrainingSettingsService), typeof(ITrainingSettingsService));
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
