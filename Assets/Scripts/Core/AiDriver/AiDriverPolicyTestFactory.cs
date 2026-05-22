using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.AiDriver.Policy;
using UnityPpoRacingTrainer.Core.AiDriver.Policy.Scenarios;
using Unidad.Core.Testing;

namespace UnityPpoRacingTrainer.Core.AiDriver
{
    internal sealed class AiDriverPolicyTestFactory : ISystemTestFactory
    {
        public Type[] TestedServices => new[] { typeof(IAiDriverPolicyService) };

        public object CreateForTesting(TestDependencies deps) => null;

        public IEnumerable<ITestScenario> GetScenarios()
        {
            yield return new MlAgentHeuristicLapScenario();
            yield return new RealisticLoopInferenceScenario();
            yield return new AuthoredOnlyClosureLoopScenario();
        }
    }
}
