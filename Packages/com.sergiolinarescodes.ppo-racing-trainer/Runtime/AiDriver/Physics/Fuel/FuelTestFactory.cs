using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.AiDriver.Physics.Fuel.Scenarios;
using Unidad.Core.Testing;

namespace UnityPpoRacingTrainer.Core.AiDriver.Physics.Fuel
{
    internal sealed class FuelTestFactory : ISystemTestFactory
    {
        public Type[] TestedServices => new[] { typeof(IFuelService) };

        public object CreateForTesting(TestDependencies deps) => null;

        public IEnumerable<ITestScenario> GetScenarios()
        {
            yield return new FuelDepletionLiftCoastScenario();
            yield return new FuelMassEffectScenario();
        }
    }
}
