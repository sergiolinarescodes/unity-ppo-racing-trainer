using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.AiDriver.Training;
using UnityPpoRacingTrainer.Core.AiDriver.Versions.Manifest;
using Reflex.Core;
using Unidad.Core.Bootstrap;
using Unidad.Core.Testing;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.AiDriver.Versions
{
    /// <summary>
    /// Wires the version registry + the active <see cref="IAiDriverVersionProfile"/>.
    /// Must be added to <c>TrainerBootstrap.RegisterInstallers</c> BEFORE
    /// <c>AiDriverPolicySystemInstaller</c> so the policy service can consume
    /// the bound profile in its ctor.
    ///
    /// Every version is manifest-driven (Phase 4+): each
    /// <see cref="AiDriverVersion"/> entry maps to a JSON file under
    /// <c>Assets/_Bootstrap/Configs/Versions/</c>, loaded by
    /// <see cref="VersionManifestLoader"/>. Adding a new snapshot is two steps:
    /// add an entry to <see cref="VersionEnumMap"/> below and drop a new
    /// <c>&lt;id&gt;.json</c> alongside <c>latest.json</c>. No new code-side
    /// profile class needed — <see cref="ManifestBackedVersionProfile"/>
    /// adapts any well-formed manifest to <see cref="IAiDriverVersionProfile"/>.
    /// </summary>
    public sealed class AiDriverVersionsSystemInstaller : ISystemInstaller
    {
        // Picker between Unity-serialized AiDriverVersion enum and the manifest
        // file's version_id string. Add a new (enum, id) pair when freezing a
        // new snapshot; the corresponding <id>.json manifest is the rest of
        // the work.
        private static readonly (AiDriverVersion Enum, string Id)[] VersionEnumMap =
        {
            (AiDriverVersion.Latest, "latest"),
            (AiDriverVersion.V1, "v1"),
        };

        private readonly AiDriverVersion _activeVersion;

        public AiDriverVersionsSystemInstaller(AiDriverVersion activeVersion)
        {
            _activeVersion = activeVersion;
        }

        public void Install(ContainerBuilder builder)
        {
            // Registry holds one ManifestBackedVersionProfile per known
            // version_id, keyed by the legacy AiDriverVersion enum. Each
            // profile captures a Func<IRewardShaper> instead of resolving
            // up-front to dodge the DI cycle (profile → shaper →
            // IActiveStageProfile → profile). NullRewardShaper.Instance is
            // the fallback when no real shaper is registered (player /
            // inference builds with AIDRIVER_TRAINING off).
            builder.AddSingleton(c =>
            {
                var registry = new AiDriverVersionRegistry();
                var manifests = c.TryResolveOptional<IReadOnlyDictionary<string, VersionManifest>>();
                if (manifests == null)
                {
                    Debug.LogError("[AiDriverVersionsSystemInstaller] manifest dictionary missing — VersionManifestSystemInstaller must precede this installer.");
                    return registry;
                }
                foreach (var (versionEnum, versionId) in VersionEnumMap)
                {
                    if (!manifests.TryGetValue(versionId, out var manifest))
                    {
                        Debug.LogError($"[AiDriverVersionsSystemInstaller] manifest '{versionId}.json' missing — {versionEnum} will not resolve.");
                        continue;
                    }
                    var profile = new ManifestBackedVersionProfile(
                        manifest,
                        () => c.TryResolveOptional<IRewardShaper>() ?? NullRewardShaper.Instance,
                        versionEnum);
                    registry.Register(versionEnum, profile);
                }
                return registry;
            }, typeof(AiDriverVersionRegistry));

            // The active profile — directly injectable into policy + bootstrap.
            // Lookup goes through the registry so the picker logic stays in
            // one place.
            var captured = _activeVersion;
            builder.AddSingleton(c =>
            {
                var registry = c.Resolve<AiDriverVersionRegistry>();
                return registry.Get(captured);
            }, typeof(IAiDriverVersionProfile));
        }

        public ISystemTestFactory CreateTestFactory() => new AiDriverVersionsTestFactory();
    }
}
