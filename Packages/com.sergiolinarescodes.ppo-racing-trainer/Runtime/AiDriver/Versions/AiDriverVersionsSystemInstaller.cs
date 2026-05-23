using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.AiDriver.Policy.Observation;
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
                if (manifests == null || manifests.Count == 0)
                {
                    // Consumer with no manifest infrastructure (e.g. game
                    // project pulling the package as a UPM dep). Register a
                    // built-in profile backed by VersionManifest's canonical
                    // defaults — the same defaults latest.json was authored
                    // from. Trainer projects override by registering
                    // VersionManifestSystemInstaller with disk-loaded
                    // manifests before this installer.
                    Debug.Log("[AiDriverVersionsSystemInstaller] no manifests registered; registering built-in default 'latest' profile.");
                    var builtIn = new ManifestBackedVersionProfile(
                        new VersionManifest(),
                        () => c.TryResolveOptional<IRewardShaper>() ?? NullRewardShaper.Instance);
                    registry.Register("latest", builtIn);
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

                // ONNX-by-name fallback: activeVersionId can be set to the
                // bare filename (no extension) of any *.onnx under
                // Resources/AiDriver/Policies/ — useful for spot-checking a
                // freshly dropped policy without authoring a manifest. The
                // base manifest stays "latest"; only OnnxResourcePath and
                // VersionId are swapped. See OnnxOverrideVersionProfile for
                // the observation-shape caveat.
                if (registry.TryGet("latest", out var fallback))
                {
                    string onnxResourcePath = TryResolveOnnxByName(captured);
                    if (onnxResourcePath != null)
                    {
                        Debug.Log(
                            $"[AiDriverVersionsSystemInstaller] active version id '{captured}' resolved by ONNX-name fallback " +
                            $"→ onnxResourcePath='{onnxResourcePath}', other settings from 'latest'.");
                        return new OnnxOverrideVersionProfile(fallback, captured, onnxResourcePath);
                    }

                    Debug.LogError(
                        $"[AiDriverVersionsSystemInstaller] active version id '{captured}' not found in manifest dictionary and " +
                        "no matching ONNX under Resources/AiDriver/Policies/. Falling back to 'latest'. Check TrainerBootstrap.activeVersionId.");
                    return fallback;
                }
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
                var registry = c.TryResolveOptional<IObservationWriterRegistry>();
                string id = profile.Manifest?.CodeModules?.ObservationWriter ?? "RacingV1";
                if (registry != null)
                {
                    if (registry.TryGet(id, out var writer)) return writer;
                    if (registry.TryGet("RacingV1", out var fallback))
                    {
                        Debug.LogError(
                            $"[AiDriverVersionsSystemInstaller] manifest references observation writer id '{id}' " +
                            "but no IObservationWriter with that id is registered. Falling back to RacingV1.");
                        return fallback;
                    }
                }
                // Consumer skipped VersionManifestSystemInstaller — fall
                // back to the canonical RacingV1 writer directly. Same
                // instance VersionManifestSystemInstaller would seed the
                // registry with.
                return RacingV1ObservationWriter.Instance;
            }, typeof(IObservationWriter));
        }

        public ISystemTestFactory CreateTestFactory() => new AiDriverVersionsTestFactory();

        // Editor: hit disk so a freshly added ONNX is picked up without
        // restarting. Player: trust the name and let Resources.Load fail
        // softly downstream if the file wasn't packaged.
        private static string TryResolveOnnxByName(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
#if UNITY_EDITOR
            string abs = System.IO.Path.Combine(Application.dataPath, "Resources", "AiDriver", "Policies", id + ".onnx");
            return System.IO.File.Exists(abs) ? "AiDriver/Policies/" + id : null;
#else
            return "AiDriver/Policies/" + id;
#endif
        }
    }
}
