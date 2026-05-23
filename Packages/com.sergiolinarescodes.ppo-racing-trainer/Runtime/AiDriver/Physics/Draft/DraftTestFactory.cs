using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.AiDriver.Physics.Draft.Scenarios;
using Unidad.Core.Testing;

namespace UnityPpoRacingTrainer.Core.AiDriver.Physics.Draft
{
    internal sealed class DraftTestFactory : ISystemTestFactory
    {
        public Type[] TestedServices => new[] { typeof(IDraftService) };

        public object CreateForTesting(TestDependencies deps) => null;

        public IEnumerable<ITestScenario> GetScenarios()
        {
            yield return new TwoCarSlipstreamScenario();
        }
    }
}
