using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.AiDriver.Loop;
using UnityPpoRacingTrainer.Core.AiDriver.Physics.Tires;
using Reflex.Core;
using Unidad.Core.Bootstrap;
using Unidad.Core.EventBus;
using Unidad.Core.Testing;

namespace UnityPpoRacingTrainer.Core.Track.Authoring.CircuitProfiles
{
    public sealed class CircuitTireProfileSystemInstaller : ISystemInstaller
    {
        public void Install(ContainerBuilder builder)
        {
            builder.AddSingleton(c => new CircuitTireProfileService(
                    c.Resolve<IEventBus>(),
                    c.Resolve<ITrackQueryService>(),
                    c.Resolve<ITirePhysicsService>()),
                typeof(ICircuitTireProfileService));
        }

        public ISystemTestFactory CreateTestFactory() => new CircuitTireProfileTestFactory();
    }

    internal sealed class CircuitTireProfileTestFactory : ISystemTestFactory
    {
        public Type[] TestedServices => new[] { typeof(ICircuitTireProfileService) };
        public object CreateForTesting(TestDependencies deps) => null;
        public IEnumerable<ITestScenario> GetScenarios()
        {
            yield return new Scenarios.CircuitTireStressHeatmapScenario();
        }
    }
}
