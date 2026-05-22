using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.AiDriver.Race.Scenarios;
using Unidad.Core.Testing;

namespace UnityPpoRacingTrainer.Core.AiDriver.Race
{
    internal sealed class RaceStateTestFactory : ISystemTestFactory
    {
        public Type[] TestedServices => new[] { typeof(IRaceStateService) };

        public object CreateForTesting(TestDependencies deps) => null;

        public IEnumerable<ITestScenario> GetScenarios()
        {
            yield return new OvertakeDetectionScenario();
        }
    }
}
