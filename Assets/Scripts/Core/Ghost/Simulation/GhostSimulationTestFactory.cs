using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.Ghost.Simulation.Scenarios;
using Unidad.Core.Testing;

namespace UnityPpoRacingTrainer.Core.Ghost.Simulation
{
    internal sealed class GhostSimulationTestFactory : ISystemTestFactory
    {
        public Type[] TestedServices => new[] { typeof(IGhostDriverService) };

        public object CreateForTesting(TestDependencies deps) => null;

        public IEnumerable<ITestScenario> GetScenarios()
        {
            yield return new GhostDriverSnapshotShapeScenario();
        }
    }
}
