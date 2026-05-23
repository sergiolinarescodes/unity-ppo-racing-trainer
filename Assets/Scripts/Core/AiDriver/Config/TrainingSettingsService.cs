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
        private readonly string _activeVersionId;
        private TrainingSettings _current;

        public TrainingSettingsService(string activeVersionId)
        {
            _activeVersionId = string.IsNullOrEmpty(activeVersionId) ? "latest" : activeVersionId;
            _current = LoadFromManifest(_activeVersionId);
        }

        public TrainingSettings Current => _current;

        public void Reload() => _current = LoadFromManifest(_activeVersionId);

        private static TrainingSettings LoadFromManifest(string activeVersionId)
        {
            var manifests = VersionManifestLoader.LoadAll();
            if (!manifests.TryGetValue(activeVersionId, out var m))
            {
                Debug.LogWarning($"[TrainingSettings] manifest '{activeVersionId}' not found under {VersionManifestLoader.DefaultRelativeFolder}; using baked defaults.");
                return new TrainingSettings();
            }
            // Observation section is frozen per ONNX checkpoint — bake the
            // C# defaults rather than projecting the manifest's values, so a
            // hand-edit of the JSON can't silently reshape the runtime sensor.
            // The dashboard still displays the manifest's observation block
            // for inspection.
            var settings = new TrainingSettings
            {
                SchemaVersion = m.SchemaVersion,
                Episode = m.Episode,
                Physics = m.Physics,
                TirePhysics = m.TirePhysics,
                RewardShaper = m.RewardShaper,
                TrackGeometry = m.TrackGeometry,
                Observation = new ObservationSettings(),
            };
            Debug.Log($"[TrainingSettings] projected from manifest '{activeVersionId}' (schemaVersion={settings.SchemaVersion}).");
            WarnOnFrozenObservationDivergence(m.Observation);
            return settings;
        }

        // Observation is frozen per ONNX checkpoint. LoadFromManifest discards
        // the manifest's observation block at projection time (Observation =
        // new ObservationSettings()), so the warning here is the last visible
        // signal that a manifest carries divergent values — purely advisory.
        private static void WarnOnFrozenObservationDivergence(ObservationSettings loaded)
        {
            var baked = new ObservationSettings();
            if (loaded.FloatsPerFrame != baked.FloatsPerFrame
                || loaded.StackedFrames != baked.StackedFrames
                || loaded.WallRayCount != baked.WallRayCount
                || loaded.LookaheadAnchors != baked.LookaheadAnchors
                || loaded.OpponentRayCount != baked.OpponentRayCount)
            {
                Debug.LogWarning("[TrainingSettings] manifest observation.* diverges from the baked schema. Mutation ignored at projection time. Edit the C# layout + retrain the ONNX to actually change observation shape.");
            }
        }
    }
}
