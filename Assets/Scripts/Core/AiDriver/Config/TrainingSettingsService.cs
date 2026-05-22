using System;
using UnityEngine;
using UnityPpoRacingTrainer.Core.AiDriver.Versions.Manifest;

namespace UnityPpoRacingTrainer.Core.AiDriver.Config
{
    /// <summary>
    /// Loads <see cref="TrainingSettings"/> by projecting the canonical
    /// <c>Assets/_Bootstrap/Configs/Versions/latest.json</c> manifest into
    /// the legacy sub-record shape. The dashboard at <c>/settings</c> edits
    /// the manifest; downstream services (<c>RewardShaper</c>,
    /// <c>TirePhysicsService</c>, etc.) still consume <c>ITrainingSettingsService</c>
    /// without knowing where the values came from.
    ///
    /// Phase 3: <c>settings.json</c> at the repo root is retired; this
    /// service no longer reads it. The manifest's <c>physics</c> /
    /// <c>tirePhysics</c> / <c>rewardShaper</c> / <c>trackGeometry</c> /
    /// <c>observation</c> / <c>episode</c> sub-records are the same POCO
    /// types as <see cref="TrainingSettings"/>'s, so the projection is a
    /// plain field copy — no JSON re-parse.
    /// </summary>
    internal sealed class TrainingSettingsService : ITrainingSettingsService
    {
        private const string ActiveVersionId = "latest";

        private TrainingSettings _current;

        public TrainingSettingsService()
        {
            _current = LoadFromManifest();
        }

        public TrainingSettings Current => _current;

        public void Reload() => _current = LoadFromManifest();

        private static TrainingSettings LoadFromManifest()
        {
            var manifests = VersionManifestLoader.LoadAll();
            if (!manifests.TryGetValue(ActiveVersionId, out var m))
            {
                Debug.LogWarning($"[TrainingSettings] manifest '{ActiveVersionId}' not found under {VersionManifestLoader.DefaultRelativeFolder}; using baked defaults.");
                return new TrainingSettings();
            }
            var settings = new TrainingSettings
            {
                SchemaVersion = m.SchemaVersion,
                Episode = m.Episode,
                Physics = m.Physics,
                TirePhysics = m.TirePhysics,
                RewardShaper = m.RewardShaper,
                TrackGeometry = m.TrackGeometry,
                Observation = m.Observation,
            };
            Debug.Log($"[TrainingSettings] projected from manifest '{ActiveVersionId}' (schemaVersion={settings.SchemaVersion}).");
            WarnOnFrozenObservationDivergence(settings);
            return settings;
        }

        // The observation section is frozen per ONNX checkpoint. Warn if the
        // loaded file diverges from the baked schema — mutation never takes
        // effect, but a misleading file would confuse a future user.
        private static void WarnOnFrozenObservationDivergence(TrainingSettings settings)
        {
            var baked = new ObservationSettings();
            var loaded = settings.Observation;
            if (loaded.FloatsPerFrame != baked.FloatsPerFrame
                || loaded.StackedFrames != baked.StackedFrames
                || loaded.WallRayCount != baked.WallRayCount
                || loaded.LookaheadAnchors != baked.LookaheadAnchors
                || loaded.OpponentRayCount != baked.OpponentRayCount)
            {
                Debug.LogWarning("[TrainingSettings] observation.* in settings.json diverges from the baked schema. Frozen — mutation ignored. Edit the C# layout + retrain the ONNX to actually change observation shape.");
            }
        }
    }
}
