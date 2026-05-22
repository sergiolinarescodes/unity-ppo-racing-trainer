using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.AiDriver.Physics.Tires.Scenarios;
using Unidad.Core.Testing;

namespace UnityPpoRacingTrainer.Core.AiDriver.Physics.Tires
{
    internal sealed class TirePhysicsTestFactory : ISystemTestFactory
    {
        public Type[] TestedServices => new[] { typeof(ITirePhysicsService) };

        public object CreateForTesting(TestDependencies deps) => null;

        public IEnumerable<ITestScenario> GetScenarios()
        {
            yield return new TireWearAcrossLapScenario();
            yield return new PunctureUnderHighGScenario();
        }
    }
}
