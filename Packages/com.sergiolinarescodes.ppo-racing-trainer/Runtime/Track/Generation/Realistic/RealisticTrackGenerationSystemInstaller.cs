using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.AiDriver.Training.Generation;
using UnityPpoRacingTrainer.Core.Terrain;
using UnityPpoRacingTrainer.Core.Track.Generation.Realistic.Native;
using UnityPpoRacingTrainer.Core.Track.Generation.Realistic.Scenarios;
using UnityPpoRacingTrainer.Core.Track.Loop;
using UnityPpoRacingTrainer.Core.Track.Shape;
using Reflex.Core;
using Unidad.Core.Bootstrap;
using Unidad.Core.EventBus;
using Unidad.Core.Testing;

namespace UnityPpoRacingTrainer.Core.Track.Generation.Realistic
{
    /// <summary>
    /// Wires <see cref="RealisticTrackGenerator"/> into Reflex. Registered against
    /// both its concrete type and <see cref="IRealisticTrackGenerator"/> so any
    /// consumer (training, future race showcase, design preview) can inject the
    /// realistic generator directly.
    ///
    /// The training-side default <see cref="IProceduralLoopGenerator"/> binding is
    /// owned by <c>AiDriverTrainingSystemInstaller</c> via
    /// <c>CurriculumGeneratorSelector</c>; this installer doesn't claim that
    /// contract so player builds with <c>AIDRIVER_TRAINING</c> off can still
    /// resolve the realistic generator without pulling in the training types.
    /// </summary>
    public sealed class RealisticTrackGenerationSystemInstaller : ISystemInstaller
    {
        public void Install(ContainerBuilder builder)
        {
            builder.AddSingleton(c => new RealisticNativeCatalog(
                    c.Resolve<ITrackShapeCatalog>(),
                    c.Resolve<ITrackPieceCatalog>()),
                typeof(RealisticNativeCatalog));

            builder.AddSingleton(c => new RealisticTrackGenerator(
                    c.Resolve<IEventBus>(),
                    c.Resolve<ITrackPlacementService>(),
                    c.Resolve<IShapePlacementService>(),
                    c.Resolve<IClosedLoopService>(),
                    c.Resolve<ITrackShapeCatalog>(),
                    c.Resolve<ITrackPieceCatalog>(),
                    c.Resolve<ITerrainService>(),
                    c.Resolve<RealisticNativeCatalog>()),
                typeof(RealisticTrackGenerator),
                typeof(IRealisticTrackGenerator));
        }

        public ISystemTestFactory CreateTestFactory()
            => new RealisticTrackGenerationTestFactory();
    }

    /// <summary>
    /// Test surface for the realistic generator. Unit + integration coverage
    /// lives under <c>Assets/Scripts/Tests/Track/Generation/</c>; this factory
    /// exposes the visual scenario for the Scenario Browser.
    /// </summary>
    internal sealed class RealisticTrackGenerationTestFactory : ISystemTestFactory
    {
        public Type[] TestedServices => new[] { typeof(IRealisticTrackGenerator) };

        public object CreateForTesting(TestDependencies deps) => null;

        public IEnumerable<ITestScenario> GetScenarios()
        {
            yield return new RealisticTrackGenerationScenario();
        }
    }
}
