using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.Ghost.MainScene.Scenarios;
using Unidad.Core.Testing;

namespace UnityPpoRacingTrainer.Core.Ghost.MainScene
{
    internal sealed class MainSceneTestFactory : ISystemTestFactory
    {
        public Type[] TestedServices => new[] { typeof(IMainSceneOrchestrator) };

        public object CreateForTesting(TestDependencies deps) => null;

        public IEnumerable<ITestScenario> GetScenarios()
        {
            yield return new MainSceneOrchestratorShapeScenario();
        }
    }
}
