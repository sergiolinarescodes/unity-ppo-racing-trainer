using Reflex.Core;
using Unidad.Core.Bootstrap;
using Unidad.Core.Factory;
using Unidad.Core.Testing;

namespace UnityPpoRacingTrainer.Core.Ghost.Presentation
{
    public sealed class GhostPresentationSystemInstaller : ISystemInstaller
    {
        public void Install(ContainerBuilder builder)
        {
            builder.AddSingleton(c => new GhostCarPresenter(c.Resolve<IGameObjectFactory>()),
                typeof(IGhostCarPresenter));
            // Single instance registered against all three interfaces — ghost director
            // resolves IGhostSpawnAnimator, placement animation + kerb service resolve
            // IDropFromAirAnimator. Same animator, same easing constants.
            builder.AddSingleton(_ => new GhostSpawnAnimator(),
                typeof(IGhostSpawnAnimator),
                typeof(IDropFromAirAnimator));
        }

        public ISystemTestFactory CreateTestFactory() => new GhostPresentationTestFactory();
    }
}
