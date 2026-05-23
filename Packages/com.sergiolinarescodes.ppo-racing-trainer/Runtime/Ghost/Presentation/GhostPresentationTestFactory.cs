using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.Ghost.Presentation.Scenarios;
using Unidad.Core.Testing;

namespace UnityPpoRacingTrainer.Core.Ghost.Presentation
{
    internal sealed class GhostPresentationTestFactory : ISystemTestFactory
    {
        public Type[] TestedServices => new[] { typeof(IGhostCarPresenter), typeof(IGhostSpawnAnimator) };

        public object CreateForTesting(TestDependencies deps) => null;

        public IEnumerable<ITestScenario> GetScenarios()
        {
            yield return new GhostSpawnAnimationScenario();
        }
    }
}
