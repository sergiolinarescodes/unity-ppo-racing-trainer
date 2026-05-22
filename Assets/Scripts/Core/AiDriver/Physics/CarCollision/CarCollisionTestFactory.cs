using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.AiDriver.Physics.CarCollision.Scenarios;
using Unidad.Core.Testing;

namespace UnityPpoRacingTrainer.Core.AiDriver.Physics.CarCollision
{
    internal sealed class CarCollisionTestFactory : ISystemTestFactory
    {
        public Type[] TestedServices => new[] { typeof(ICarCollisionService) };

        public object CreateForTesting(TestDependencies deps) => null;

        public IEnumerable<ITestScenario> GetScenarios()
        {
            yield return new HeadOnAndSideSwipeScenario();
        }
    }
}
