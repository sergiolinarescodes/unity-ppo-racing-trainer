using Unidad.Core.Registry;

namespace UnityPpoRacingTrainer.Core.AiDriver.Training.Stages
{
    /// <summary>
    /// Registry keyed by <c>stage_id</c> holding every <see cref="IStageProfile"/>
    /// known to the active driver version. Populated by
    /// <c>StageProfileSystemInstaller</c>; consumed by
    /// <see cref="IActiveStageProfile"/>.
    ///
    /// Stripped-down snapshots may return a 1-profile registry; Latest
    /// returns the 6-profile curriculum registry. Wired through
    /// <c>IAiDriverVersionProfile.StageProfiles</c>.
    /// </summary>
    public interface IStageProfileRegistry : IRegistry<int, IStageProfile> { }

    public sealed class StageProfileRegistry : RegistryBase<int, IStageProfile>, IStageProfileRegistry { }
}
