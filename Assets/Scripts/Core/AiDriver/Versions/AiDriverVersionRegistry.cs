using Unidad.Core.Registry;

namespace UnityPpoRacingTrainer.Core.AiDriver.Versions
{
    /// <summary>
    /// Registry keyed by <see cref="AiDriverVersion"/> holding every known
    /// <see cref="IAiDriverVersionProfile"/>. Populated by
    /// <c>AiDriverVersionsSystemInstaller</c>; consumed by
    /// <c>TrainerBootstrap</c> + <c>AiDriverPolicyService</c> via DI.
    /// </summary>
    public sealed class AiDriverVersionRegistry : RegistryBase<AiDriverVersion, IAiDriverVersionProfile>
    {
    }
}
