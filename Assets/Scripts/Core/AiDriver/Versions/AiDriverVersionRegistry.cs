using Unidad.Core.Registry;

namespace UnityPpoRacingTrainer.Core.AiDriver.Versions
{
    /// <summary>
    /// Registry of every known <see cref="IAiDriverVersionProfile"/>, keyed by
    /// the version id string (matches the manifest's <c>versionId</c> field
    /// and the manifest filename — <c>"latest"</c>, <c>"v1"</c>, …). Populated
    /// by <c>AiDriverVersionsSystemInstaller</c> at bootstrap from the manifest
    /// dictionary; consumed by <c>TrainerBootstrap</c> + <c>AiDriverPolicyService</c>
    /// via DI. Adding a new version = drop a <c>&lt;id&gt;.json</c> under
    /// <c>Assets/_Bootstrap/Configs/Versions/</c>; no enum bump needed.
    /// </summary>
    public sealed class AiDriverVersionRegistry : RegistryBase<string, IAiDriverVersionProfile>
    {
    }
}
