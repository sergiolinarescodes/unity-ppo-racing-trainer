using UnityPpoRacingTrainer.Core.AiDriver.Config;
using UnityPpoRacingTrainer.Core.AiDriver.Loop;
using UnityPpoRacingTrainer.Core.AiDriver.Physics;
using UnityPpoRacingTrainer.Core.AiDriver.Physics.Draft;
using UnityPpoRacingTrainer.Core.AiDriver.Physics.Fuel;
using UnityPpoRacingTrainer.Core.AiDriver.Physics.Tires;
using UnityPpoRacingTrainer.Core.AiDriver.Policy;
using UnityPpoRacingTrainer.Core.AiDriver.Race;
using UnityPpoRacingTrainer.Core.AiDriver.Training;
using UnityPpoRacingTrainer.Core.AiDriver.Training.Generation;
using UnityPpoRacingTrainer.Core.AiDriver.Training.Stages;
using UnityPpoRacingTrainer.Core.Track;
using UnityPpoRacingTrainer.Core.Track.Loop;
using Reflex.Core;
using Unidad.Core.Abstractions;
using Unidad.Core.Bootstrap;
using Unidad.Core.EventBus;
using Unidad.Core.Testing;

namespace UnityPpoRacingTrainer.Core.AiDriver
{
    /// <summary>
    /// Wires <see cref="EpisodeRunner"/> as the active <see cref="IEpisodeRewardSource"/>
    /// when the <c>AIDRIVER_TRAINING</c> define is on. With the define off,
    /// <see cref="NullEpisodeRewardSource"/> stays bound — same shape, zero
    /// reward, no termination — so player builds strip the trainer without
    /// ifdefs at the call site.
    /// </summary>
    public sealed class AiDriverTrainingSystemInstaller : ISystemInstaller
    {
        public void Install(ContainerBuilder builder)
        {
#if AIDRIVER_TRAINING
            builder.AddSingleton(_ => new TrainingStageProvider(),
                typeof(IStageIdProvider));

            builder.AddSingleton(c => new EpisodeRunner(
                    c.Resolve<IEventBus>(),
                    c.Resolve<ICarSimulationService>(),
                    c.Resolve<ITrackQueryService>(),
                    c.Resolve<IClosedLoopService>(),
                    c.Resolve<ITimeProvider>(),
                    c.TryResolveOptional<IRaceCoordinator>()),
                typeof(EpisodeRunner));

            // Personality-aware reward shaping. Built only when the side-system services
            // are present. Personality is sampled per-episode and cached in
            // the shaper's accum state, so no policy reference is needed.
            builder.AddSingleton(c => new RewardShaper(
                    c.Resolve<IEventBus>(),
                    c.Resolve<ITrainingSettingsService>(),
                    c.Resolve<IActiveStageProfile>(),
                    c.TryResolveOptional<ITirePhysicsService>(),
                    c.TryResolveOptional<IFuelService>(),
                    c.TryResolveOptional<IDraftService>(),
                    c.Resolve<IRaceStateService>(),
                    c.Resolve<IClosedLoopService>(),
                    c.TryResolveOptional<IDriverPhysicsRegistry>(),
                    c.Resolve<ICarSimulationService>()),
                typeof(IRewardShaper));

            // Composite reward source: EpisodeRunner (base) plus the
            // optional personality-aware reward shaper.
            builder.AddSingleton(c => new CompositeEpisodeRewardSource(
                    c.Resolve<EpisodeRunner>(),
                    c.TryResolveOptional<IRewardShaper>(),
                    c.Resolve<ITimeProvider>()),
                typeof(IEpisodeRewardSource));

            builder.AddSingleton(c => new ShapeBasedLoopGenerator(
                    c.Resolve<IEventBus>(),
                    c.Resolve<Track.Shape.IShapePlacementService>(),
                    c.Resolve<Track.Shape.ITrackShapeCatalog>(),
                    c.Resolve<ITrackPlacementService>(),
                    c.Resolve<IClosedLoopService>()),
                typeof(ShapeBasedLoopGenerator));

            builder.AddSingleton(c => new CurriculumGeneratorSelector(
                    c.Resolve<ShapeBasedLoopGenerator>(),
                    c.Resolve<ITrackPlacementService>(),
                    c.Resolve<IClosedLoopService>()),
                typeof(CurriculumGeneratorSelector), typeof(IProceduralLoopGenerator));

            builder.AddSingleton(c => new TrainingDirector(
                    c.Resolve<IEventBus>(),
                    c.Resolve<ITrackPlacementService>(),
                    c.Resolve<IProceduralLoopGenerator>(),
                    c.Resolve<IClosedLoopService>(),
                    c.Resolve<IAiDriverPolicyService>(),
                    stageIdProvider: null,
                    coord: c.TryResolveOptional<IRaceCoordinator>()),
                typeof(TrainingDirector));
#endif
        }

        public ISystemTestFactory CreateTestFactory() => new AiDriverTrainingTestFactory();
    }
}
