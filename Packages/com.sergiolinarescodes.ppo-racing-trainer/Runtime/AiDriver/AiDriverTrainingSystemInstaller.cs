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
using UnityPpoRacingTrainer.Core.AiDriver.Training.Rewards;
using UnityPpoRacingTrainer.Core.AiDriver.Versions;
using UnityPpoRacingTrainer.Core.AiDriver.Versions.Manifest;
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
                    c.TryResolveOptional<ITirePhysicsService>(),
                    c.TryResolveOptional<IFuelService>(),
                    c.TryResolveOptional<IDraftService>(),
                    c.Resolve<IRaceStateService>(),
                    c.Resolve<IClosedLoopService>(),
                    c.TryResolveOptional<IDriverPhysicsRegistry>(),
                    c.Resolve<ICarSimulationService>()),
                typeof(IRewardShaper));

            // Resolved extra reward channels (plug-in surface). The active
            // version manifest lists channel ids; this binding turns those
            // into IRewardChannel instances by looking each id up in
            // IRewardChannelRegistry. Manifests with no entries resolve to an
            // empty list and the composite source falls through to the base +
            // shaper path unchanged.
            builder.AddSingleton(c =>
            {
                var profile = c.Resolve<IAiDriverVersionProfile>();
                var registry = c.TryResolveOptional<IRewardChannelRegistry>();
                var entries = profile.Manifest?.RewardChannels;
                if (registry == null || entries == null || entries.Count == 0)
                    return ActiveRewardChannels.Empty;
                var list = new System.Collections.Generic.List<ActiveRewardChannel>(entries.Count);
                foreach (var e in entries)
                {
                    if (registry.TryGet(e.Id, out var channel))
                    {
                        list.Add(new ActiveRewardChannel(channel));
                    }
                    else
                    {
                        UnityEngine.Debug.LogError(
                            $"[AiDriverTrainingSystemInstaller] manifest references reward channel id '{e.Id}' " +
                            "but no IRewardChannel with that id is registered. " +
                            "Register one in an ISystemInstaller before AiDriverTrainingSystemInstaller, " +
                            "or remove the entry from the manifest.");
                    }
                }
                return new ActiveRewardChannels(list);
            }, typeof(ActiveRewardChannels));

            // Composite reward source: EpisodeRunner (base) plus the optional
            // personality-aware reward shaper plus any manifest-driven extra
            // reward channels.
            builder.AddSingleton(c => new CompositeEpisodeRewardSource(
                    c.Resolve<EpisodeRunner>(),
                    c.TryResolveOptional<IRewardShaper>(),
                    c.Resolve<ITimeProvider>(),
                    c.TryResolveOptional<ActiveRewardChannels>()),
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
                    coord: c.TryResolveOptional<IRaceCoordinator>()),
                typeof(TrainingDirector));
#endif
        }

        public ISystemTestFactory CreateTestFactory() => new AiDriverTrainingTestFactory();
    }
}
