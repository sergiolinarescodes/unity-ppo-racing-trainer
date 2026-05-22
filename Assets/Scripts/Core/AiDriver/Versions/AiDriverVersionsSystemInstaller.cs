using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.AiDriver.Training;
using UnityPpoRacingTrainer.Core.AiDriver.Versions.Latest;
using UnityPpoRacingTrainer.Core.AiDriver.Versions.Manifest;
using UnityPpoRacingTrainer.Core.AiDriver.Versions.V1;
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
    /// Currently registers a single profile (<see cref="LatestVersionProfile"/>).
    /// When a new snapshot is frozen, register the prior canonical here under
    /// its numbered <see cref="AiDriverVersion"/> entry and keep
    /// <see cref="LatestVersionProfile"/> pointed at the new canonical.
    /// </summary>
    public sealed class AiDriverVersionsSystemInstaller : ISystemInstaller
    {
        private readonly AiDriverVersion _activeVersion;

        public AiDriverVersionsSystemInstaller(AiDriverVersion activeVersion)
        {
            _activeVersion = activeVersion;
        }

        public void Install(ContainerBuilder builder)
        {
            // LatestVersionProfile captures a Func<IRewardShaper> instead of
            // resolving up-front — resolving IRewardShaper here triggers a DI
            // cycle (RewardShaper → IActiveStageProfile → IAiDriverVersionProfile
            // → us). Deferred resolution lets every installer register first; the
            // property getter then resolves cleanly. NullRewardShaper.Instance
            // is the fallback when no shaper is registered (AIDRIVER_TRAINING
            // off in a player build).
            builder.AddSingleton(c => new LatestVersionProfile(
                    () => c.TryResolveOptional<IRewardShaper>() ?? NullRewardShaper.Instance),
                typeof(LatestVersionProfile));

            // V1 snapshot: same lazy-resolution wiring as Latest. The frozen
            // profile is registered so picking AiDriverVersion.V1 in the
            // bootstrap resolves cleanly. The matching prefab + ONNX are
            // Editor-side artifacts (see docs/snapshot-version.md).
            builder.AddSingleton(c => new V1VersionProfile(
                    () => c.TryResolveOptional<IRewardShaper>() ?? NullRewardShaper.Instance),
                typeof(V1VersionProfile));

            // Registry holds every known profile keyed by enum. Phase 2 flip:
            // when the manifest folder contains a "latest" entry, the active
            // Latest profile becomes the data-driven ManifestBackedVersionProfile
            // (sourced from Assets/_Bootstrap/Configs/Versions/latest.json).
            // Falls back to the historical C# LatestVersionProfile if the
            // manifest is missing or fails to load — ensures bootstrap never
            // hard-fails on a malformed manifest. The bit-identical parity is
            // locked by ManifestParityTests.
            builder.AddSingleton(c =>
            {
                var registry = new AiDriverVersionRegistry();
                var manifests = c.TryResolveOptional<IReadOnlyDictionary<string, VersionManifest>>();
                IAiDriverVersionProfile latest;
                if (manifests != null && manifests.TryGetValue("latest", out var latestManifest))
                {
                    latest = new ManifestBackedVersionProfile(
                        latestManifest,
                        () => c.TryResolveOptional<IRewardShaper>() ?? NullRewardShaper.Instance,
                        AiDriverVersion.Latest);
                }
                else
                {
                    Debug.LogWarning("[AiDriverVersionsSystemInstaller] manifest \"latest\" not found — falling back to C# LatestVersionProfile.");
                    latest = c.Resolve<LatestVersionProfile>();
                }
                registry.Register(AiDriverVersion.Latest, latest);
                registry.Register(AiDriverVersion.V1, c.Resolve<V1VersionProfile>());
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
