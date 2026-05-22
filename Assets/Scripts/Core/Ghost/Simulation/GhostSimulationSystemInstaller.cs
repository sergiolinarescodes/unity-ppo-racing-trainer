using UnityPpoRacingTrainer.Core.AiDriver;
using UnityPpoRacingTrainer.Core.AiDriver.Loop;
using UnityPpoRacingTrainer.Core.AiDriver.Physics;
using UnityPpoRacingTrainer.Core.AiDriver.Policy;
using UnityPpoRacingTrainer.Core.AiDriver.Versions;
using UnityPpoRacingTrainer.Core.Terrain;
using UnityPpoRacingTrainer.Core.Track;
using Reflex.Core;
using Unidad.Core.Abstractions;
using Unidad.Core.Bootstrap;
using Unidad.Core.EventBus;
using Unidad.Core.Testing;

namespace UnityPpoRacingTrainer.Core.Ghost.Simulation
{
    public sealed class GhostSimulationSystemInstaller : ISystemInstaller
    {
        public void Install(ContainerBuilder builder)
        {
            builder.AddSingleton(c => new GhostDriverService(
                    c.Resolve<IEventBus>(),
                    c.Resolve<ICarSimulationService>(),
                    c.Resolve<ITrackQueryService>(),
                    c.Resolve<ITrackPlacementService>(),
                    c.Resolve<IAiDriverVersionProfile>(),
                    c.Resolve<IAiDriverPolicyService>(),
                    c.Resolve<ITerrainService>(),
                    c.TryResolveOptional<IDriverPhysicsRegistry>()),
                typeof(IGhostDriverService),
                typeof(ITickable));
        }

        public ISystemTestFactory CreateTestFactory() => new GhostSimulationTestFactory();
    }
}
