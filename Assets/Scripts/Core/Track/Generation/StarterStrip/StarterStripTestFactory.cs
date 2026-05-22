using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.Track.Generation.StarterStrip.Scenarios;
using Unidad.Core.Testing;

namespace UnityPpoRacingTrainer.Core.Track.Generation.StarterStrip
{
    internal sealed class StarterStripTestFactory : ISystemTestFactory
    {
        public Type[] TestedServices => new[] { typeof(IStarterStripGenerator) };

        public object CreateForTesting(TestDependencies deps) => null;

        public IEnumerable<ITestScenario> GetScenarios()
        {
            yield return new StarterStripGenerationScenario();
        }
    }
}
