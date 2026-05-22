using Unidad.Core.Registry;

namespace UnityPpoRacingTrainer.Core.AiDriver.Versions.Manifest
{
    /// <summary>
    /// Plug-in strategy interfaces consumed by <see cref="ManifestBackedVersionProfile"/>
    /// via string-keyed registries. Each concrete strategy is registered once
    /// in DI by id; manifests reference strategies by their id string.
    ///
    /// Phase 1: interfaces + empty registries only. The current code paths
    /// still inline-instantiate equivalents; later phases (3 = rewards,
    /// 4 = stages, 5 = observations) populate these registries and switch
    /// consumers to resolve through them.
    /// </summary>
    public interface IRewardChannel
    {
        string Id { get; }
    }

    public interface IPhysicsModel
    {
        string Id { get; }
    }

    public interface IObservationWriter
    {
        string Id { get; }
        int FloatsPerFrame { get; }

        /// <summary>
        /// Stable, content-derived hash of the layout this writer emits.
        /// Manifests pin this in <c>observation.layout_hash</c>; the snapshot
        /// test loads every manifest and asserts the writer's hash matches.
        /// Drift between writer code and any version's pinned hash fails CI.
        /// </summary>
        string LayoutHash { get; }
    }

    public interface IRewardChannelRegistry : IRegistry<string, IRewardChannel> { }
    public sealed class RewardChannelRegistry : RegistryBase<string, IRewardChannel>, IRewardChannelRegistry { }

    public interface IPhysicsModelRegistry : IRegistry<string, IPhysicsModel> { }
    public sealed class PhysicsModelRegistry : RegistryBase<string, IPhysicsModel>, IPhysicsModelRegistry { }

    public interface IObservationWriterRegistry : IRegistry<string, IObservationWriter> { }
    public sealed class ObservationWriterRegistry : RegistryBase<string, IObservationWriter>, IObservationWriterRegistry { }
}
