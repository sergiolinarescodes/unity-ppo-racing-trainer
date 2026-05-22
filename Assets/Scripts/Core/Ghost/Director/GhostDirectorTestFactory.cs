using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.Ghost.Director.Scenarios;
using Unidad.Core.Testing;

namespace UnityPpoRacingTrainer.Core.Ghost.Director
{
    internal sealed class GhostDirectorTestFactory : ISystemTestFactory
    {
        public Type[] TestedServices => new[] { typeof(IGameSceneDirector) };

        public object CreateForTesting(TestDependencies deps) => null;

        public IEnumerable<ITestScenario> GetScenarios()
        {
            yield return new GhostDirectorStateShapeScenario();
        }
    }
}
