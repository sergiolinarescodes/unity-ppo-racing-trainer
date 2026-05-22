using System;
using System.Collections.Generic;
using System.Linq;
using UnityPpoRacingTrainer.Core.AiDriver.Versions;
using Reflex.Core;
using Unidad.Core.Bootstrap;
using Unidad.Core.Testing;

namespace UnityPpoRacingTrainer.Core.AiDriver.Training.Stages
{
    /// <summary>
    /// Wires <see cref="IActiveStageProfile"/>. The stage-profile registry
    /// itself is owned by <see cref="IAiDriverVersionProfile.StageProfiles"/>
    /// (stripped-down snapshots may return a 1-profile registry; Latest
    /// returns the 6-profile curriculum) — this installer reads it via DI and does not
    /// build a global registry.
    ///
    /// Install order: AFTER <c>AiDriverVersionsSystemInstaller</c> (the
    /// active <see cref="IAiDriverVersionProfile"/> must be resolvable) and
    /// BEFORE <c>AiDriverPolicySystemInstaller</c> / <c>AiDriverTrainingSystemInstaller</c>
    /// so PolicyService and RewardShaper can consume <see cref="IActiveStageProfile"/>
    /// in their ctors.
    /// </summary>
    public sealed class StageProfileSystemInstaller : ISystemInstaller
    {
        public void Install(ContainerBuilder builder)
        {
            builder.AddSingleton(c => new ActiveStageProfile(
                    c.Resolve<IAiDriverVersionProfile>(),
                    c.TryResolveOptional<IStageIdProvider>()),
                typeof(IActiveStageProfile));
        }

        public ISystemTestFactory CreateTestFactory() => new StageProfileTestFactory();
    }

    internal sealed class StageProfileTestFactory : ISystemTestFactory
    {
        public Type[] TestedServices => new[]
        {
            typeof(IStageProfile),
            typeof(IStageProfileRegistry),
            typeof(IActiveStageProfile),
        };

        public object CreateForTesting(TestDependencies deps) => null;

        public IEnumerable<ITestScenario> GetScenarios() => Enumerable.Empty<ITestScenario>();
    }
}
