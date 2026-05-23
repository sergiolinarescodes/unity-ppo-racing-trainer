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
    /// Every version is manifest-driven (Phase 4+) and string-id keyed
    /// (Phase 6). Every <c>&lt;id&gt;.json</c> under
    /// <c>Assets/_Bootstrap/Configs/Versions/</c> is auto-registered as one
    /// <see cref="ManifestBackedVersionProfile"/>; the picker is the
    /// <c>activeVersionId</c> string passed in from <c>TrainerBootstrap</c>.
    /// Adding a new version is one step: drop a new <c>&lt;id&gt;.json</c>.
    /// </summary>
    public sealed class AiDriverVersionsSystemInstaller : ISystemInstaller
    {
        private readonly string _activeVersionId;

        public AiDriverVersionsSystemInstaller(string activeVersionId)
        {
            _activeVersionId = string.IsNullOrEmpty(activeVersionId) ? "latest" : activeVersionId;
        }

        public void Install(ContainerBuilder builder)
        {
            // Registry holds one ManifestBackedVersionProfile per manifest in
            // the loaded dictionary, keyed by versionId string. Each profile
            // captures a Func<IRewardShaper> instead of resolving up-front to
            // dodge the DI cycle (profile → shaper → profile).
            // NullRewardShaper.Instance is the fallback when no
            // real shaper is registered (player / inference builds with
            // AIDRIVER_TRAINING off).
            builder.AddSingleton(c =>
            {
                var registry = new AiDriverVersionRegistry();
                var manifests = c.TryResolveOptional<IReadOnlyDictionary<string, VersionManifest>>();
                if (manifests == null)
                {
                    Debug.LogError("[AiDriverVersionsSystemInstaller] manifest dictionary missing — VersionManifestSystemInstaller must precede this installer.");
                    return registry;
                }
                foreach (var kv in manifests)
                {
                    var profile = new ManifestBackedVersionProfile(
                        kv.Value,
                        () => c.TryResolveOptional<IRewardShaper>() ?? NullRewardShaper.Instance);
                    registry.Register(kv.Key, profile);
                }
                return registry;
            }, typeof(AiDriverVersionRegistry));

            // The active profile — directly injectable into policy + bootstrap.
            // Lookup goes through the registry so the picker logic stays in
            // one place.
            var captured = _activeVersionId;
            builder.AddSingleton(c =>
            {
                var registry = c.Resolve<AiDriverVersionRegistry>();
                if (registry.TryGet(captured, out var profile)) return profile;
                Debug.LogError(
                    $"[AiDriverVersionsSystemInstaller] active version id '{captured}' not found in manifest dictionary. " +
                    "Falling back to 'latest'. Check TrainerBootstrap.activeVersionId or drop a matching <id>.json.");
                if (registry.TryGet("latest", out var fallback)) return fallback;
                throw new System.InvalidOperationException(
                    "AiDriverVersionRegistry is empty — manifests must exist under Assets/_Bootstrap/Configs/Versions/.");
            }, typeof(IAiDriverVersionProfile));

            // Active IObservationWriter: looked up by id from the active
            // version's manifest.codeModules.observationWriter. Any registered
            // writer can be selected — this is the dispatch point that lets a
            // future version pick a different observation layout without
            // touching the policy service.
            builder.AddSingleton(c =>
            {
                var profile = c.Resolve<IAiDriverVersionProfile>();
                var registry = c.Resolve<IObservationWriterRegistry>();
                string id = profile.Manifest?.CodeModules?.ObservationWriter ?? "RacingV1";
                if (registry.TryGet(id, out var writer)) return writer;
                Debug.LogError(
                    $"[AiDriverVersionsSystemInstaller] manifest references observation writer id '{id}' " +
                    "but no IObservationWriter with that id is registered. Falling back to RacingV1.");
                if (registry.TryGet("RacingV1", out var fallback)) return fallback;
                throw new System.InvalidOperationException(
                    "IObservationWriterRegistry is empty — VersionManifestSystemInstaller must precede AiDriverVersionsSystemInstaller.");
            }, typeof(IObservationWriter));
        }

        public ISystemTestFactory CreateTestFactory() => new AiDriverVersionsTestFactory();
    }
}
