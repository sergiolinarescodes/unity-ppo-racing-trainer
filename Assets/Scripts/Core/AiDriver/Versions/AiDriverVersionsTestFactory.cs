using System;
using System.Collections.Generic;
using System.Linq;
using Unidad.Core.Testing;

namespace UnityPpoRacingTrainer.Core.AiDriver.Versions
{
    /// <summary>
    /// Test factory for the AI driver versions subsystem. Scenarios live in
    /// <c>Runtime/AiDriver/Versions/Scenarios/</c> and are yielded here so
    /// the Scenario Browser + <c>AllSystemScenariosTests</c> discover them.
    /// </summary>
    internal sealed class AiDriverVersionsTestFactory : ISystemTestFactory
    {
        public Type[] TestedServices => new[]
        {
            typeof(IAiDriverVersionProfile),
            typeof(AiDriverVersionRegistry),
        };

        public object CreateForTesting(TestDependencies deps) => null;

        public IEnumerable<ITestScenario> GetScenarios() => Enumerable.Empty<ITestScenario>();
    }
}
