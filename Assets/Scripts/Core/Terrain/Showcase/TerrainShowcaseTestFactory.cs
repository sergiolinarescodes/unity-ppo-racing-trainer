using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.Terrain.Scenarios;
using Unidad.Core.Testing;

namespace UnityPpoRacingTrainer.Core.Terrain.Showcase
{
    internal sealed class TerrainShowcaseTestFactory : ISystemTestFactory
    {
        public Type[] TestedServices => new[] { typeof(ITerrainShowcaseService) };

        public object CreateForTesting(TestDependencies deps) => null;

        public IEnumerable<ITestScenario> GetScenarios()
        {
            yield return new TerrainShowcaseScenario();
        }
    }
}
