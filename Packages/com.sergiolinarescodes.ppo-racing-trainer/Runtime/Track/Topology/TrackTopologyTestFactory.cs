using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.Track.Topology.Scenarios;
using Unidad.Core.Testing;

namespace UnityPpoRacingTrainer.Core.Track.Topology
{
    internal sealed class TrackTopologyTestFactory : ISystemTestFactory
    {
        public Type[] TestedServices => new[] { typeof(ITrackEndingService) };

        public object CreateForTesting(TestDependencies deps) => null;

        public IEnumerable<ITestScenario> GetScenarios()
        {
            yield return new TrackEndingScenario();
        }
    }
}
