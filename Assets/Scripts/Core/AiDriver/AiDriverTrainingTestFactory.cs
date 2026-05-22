using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.AiDriver.Training;
using UnityPpoRacingTrainer.Core.AiDriver.Training.Generation;
using UnityPpoRacingTrainer.Core.AiDriver.Training.Scenarios;
using Unidad.Core.Testing;

namespace UnityPpoRacingTrainer.Core.AiDriver
{
    /// <summary>
    /// Test surface for the training system. Reward + episode-end logic is
    /// unit-tested via <see cref="EpisodeRewardCalculator"/> and
    /// <see cref="EpisodeRunner"/> fixtures; the procedural-loop generator gets
    /// one visual scenario plus its own unit tests under
    /// <c>Assets/Scripts/Tests/AiDriver/</c>.
    /// </summary>
    internal sealed class AiDriverTrainingTestFactory : ISystemTestFactory
    {
        public Type[] TestedServices => new[]
        {
            typeof(IEpisodeRewardSource),
            typeof(IProceduralLoopGenerator),
        };

        public object CreateForTesting(TestDependencies deps) => null;

        public IEnumerable<ITestScenario> GetScenarios()
        {
            yield return new ProceduralLoopGenerationScenario();
        }
    }
}
