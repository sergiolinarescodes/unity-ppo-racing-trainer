using UnityPpoRacingTrainer.Core.AiDriver.Loop;
using UnityPpoRacingTrainer.Core.Terrain;
using UnityPpoRacingTrainer.Core.Track;
using UnityPpoRacingTrainer.Core.Track.Loop;
using Reflex.Core;
using Unidad.Core.Bootstrap;
using Unidad.Core.EventBus;
using Unidad.Core.Testing;

namespace UnityPpoRacingTrainer.Core.AiDriver
{
    /// <summary>
    /// Wires the AI driver's "map sense" services. Owns <see cref="ITrackQueryService"/>
    /// which sits on top of the Track system's <see cref="IClosedLoopService"/> and
    /// converts the closed loop into projection + lookahead queries the policy
    /// observation assembler can read each step. Also picks up the optional
    /// <see cref="ITrackCollisionService"/> so projections carry the kerb-vs-asphalt
    /// surface kind alongside the centerline data.
    /// </summary>
    public sealed class AiDriverLoopSystemInstaller : ISystemInstaller
    {
        public void Install(ContainerBuilder builder)
        {
            builder.AddSingleton(c =>
            {
                // Optional resolves — keeps the service usable from unit tests
                // and scenarios that wire only a minimal subset of the Track
                // system. When any of these is missing, the open-ribbon
                // fallback stays disabled and the service falls back to its
                // legacy closed-loop-only behaviour.
                T TryResolve<T>() where T : class
                {
                    try { return c.Resolve<T>(); }
                    catch { return null; }
                }

                return (ITrackQueryService)new TrackQueryService(
                    c.Resolve<IEventBus>(),
                    c.Resolve<IClosedLoopService>(),
                    c.Resolve<ITrackCollisionService>(),
                    TryResolve<ITrackPlacementService>(),
                    TryResolve<ITrackPieceCatalog>(),
                    TryResolve<ITerrainService>());
            }, typeof(ITrackQueryService));
        }

        public ISystemTestFactory CreateTestFactory() => new AiDriverLoopTestFactory();
    }
}
