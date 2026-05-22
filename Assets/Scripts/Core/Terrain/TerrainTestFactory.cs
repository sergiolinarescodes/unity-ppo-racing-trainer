using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.Terrain.Scenarios;
using Unidad.Core.Testing;

namespace UnityPpoRacingTrainer.Core.Terrain
{
    internal sealed class TerrainTestFactory : ISystemTestFactory
    {
        public Type[] TestedServices => new[] { typeof(ITerrainService), typeof(ITerrainMeshBuilder) };

        public object CreateForTesting(TestDependencies deps) => new TerrainService(deps.EventBus);

        public IEnumerable<ITestScenario> GetScenarios()
        {
            yield return new TerrainGenerationScenario();
        }
    }
}
