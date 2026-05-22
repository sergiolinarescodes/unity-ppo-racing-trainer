using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.AiDriver.Physics;
using UnityPpoRacingTrainer.Core.AiDriver.Physics.Scenarios;
using Unidad.Core.Testing;

namespace UnityPpoRacingTrainer.Core.AiDriver
{
    internal sealed class AiDriverPhysicsTestFactory : ISystemTestFactory
    {
        public Type[] TestedServices => new[] { typeof(ICarSimulationService) };

        public object CreateForTesting(TestDependencies deps) => null;

        public IEnumerable<ITestScenario> GetScenarios()
        {
            yield return new HeuristicLapScenario();
        }
    }
}
