using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.AiDriver.Loop;
using UnityPpoRacingTrainer.Core.Ghost.Kerbs.Scenarios;
using UnityPpoRacingTrainer.Core.Ghost.Presentation;
using UnityPpoRacingTrainer.Core.Ghost.Simulation;
using UnityPpoRacingTrainer.Core.Track;
using Reflex.Core;
using Unidad.Core.Abstractions;
using Unidad.Core.Bootstrap;
using Unidad.Core.EventBus;
using Unidad.Core.Factory;
using Unidad.Core.Testing;

namespace UnityPpoRacingTrainer.Core.Ghost.Kerbs
{
    public sealed class RacingLineKerbsSystemInstaller : ISystemInstaller
    {
        public void Install(ContainerBuilder builder)
        {
            // Recorder buffers per-lap polylines and ticks alongside the ghost.
            // Registered as ITickable so UnidadBootstrap.ResolveTickables picks
            // it up via container.All<ITickable>().
            builder.AddSingleton(c => new RacingLineRecorder(
                    c.Resolve<IEventBus>(),
                    c.Resolve<IGhostDriverService>()),
                typeof(IRacingLineRecorder),
                typeof(RacingLineRecorder),
                typeof(ITickable));

            // Service emits + clears dynamic kerb instances. Subscribed to lap +
            // topology events via SystemServiceBase; no Tick of its own.
            builder.AddSingleton(c => new RacingLineKerbService(
                    c.Resolve<IEventBus>(),
                    c.Resolve<IRacingLineRecorder>(),
                    c.Resolve<ITrackQueryService>(),
                    c.Resolve<ITrackCollisionService>(),
                    c.Resolve<IGameObjectFactory>(),
                    c.Resolve<IDropFromAirAnimator>()),
                typeof(IRacingLineKerbService),
                typeof(RacingLineKerbService));
        }

        public ISystemTestFactory CreateTestFactory() => new RacingLineKerbsTestFactory();
    }

    internal sealed class RacingLineKerbsTestFactory : ISystemTestFactory
    {
        public Type[] TestedServices => new[]
        {
            typeof(IRacingLineRecorder),
            typeof(IRacingLineKerbService)
        };

        public object CreateForTesting(TestDependencies deps) => null;

        public IEnumerable<ITestScenario> GetScenarios()
        {
            yield return new DynamicKerbAfterLapScenario();
        }
    }
}
