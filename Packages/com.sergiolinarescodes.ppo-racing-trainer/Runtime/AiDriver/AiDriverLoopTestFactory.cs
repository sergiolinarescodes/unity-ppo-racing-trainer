using System;
using System.Collections.Generic;
using System.Linq;
using UnityPpoRacingTrainer.Core.AiDriver.Loop;
using Unidad.Core.Testing;

namespace UnityPpoRacingTrainer.Core.AiDriver
{
    internal sealed class AiDriverLoopTestFactory : ISystemTestFactory
    {
        public Type[] TestedServices => new[] { typeof(ITrackQueryService) };

        public object CreateForTesting(TestDependencies deps) => null;

        public IEnumerable<ITestScenario> GetScenarios() => Enumerable.Empty<ITestScenario>();
    }
}
