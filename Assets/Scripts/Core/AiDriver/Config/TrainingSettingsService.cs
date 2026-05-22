using System;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.AiDriver.Config
{
    /// <summary>
    /// Reads <c>settings.json</c> at the project root (sibling of <c>Assets/</c>)
    /// and exposes the parsed <see cref="TrainingSettings"/>. Tolerates missing
    /// file, malformed JSON, and partial fields by falling back to baked defaults.
    /// </summary>
    internal sealed class TrainingSettingsService : ITrainingSettingsService
    {
        private const string SettingsFileName = "settings.json";

        private readonly string _settingsPath;
        private TrainingSettings _current;

        public TrainingSettingsService()
        {
            // Application.dataPath ends with .../Assets; settings.json sits next to it.
            _settingsPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", SettingsFileName));
            _current = LoadFromDisk();
        }

        public TrainingSettings Current => _current;

        public void Reload() => _current = LoadFromDisk();

        private TrainingSettings LoadFromDisk()
        {
            if (!File.Exists(_settingsPath))
            {
                Debug.Log($"[TrainingSettings] {SettingsFileName} not found at {_settingsPath}; using baked defaults.");
                return new TrainingSettings();
            }

            string json;
            try
            {
                json = File.ReadAllText(_settingsPath);
            }
            catch (IOException ex)
            {
                Debug.LogError($"[TrainingSettings] Failed to read {SettingsFileName}: {ex.Message}; using baked defaults.");
                return new TrainingSettings();
            }

            try
            {
                var settings = JsonConvert.DeserializeObject<TrainingSettings>(json, new JsonSerializerSettings
                {
                    MissingMemberHandling = MissingMemberHandling.Ignore,
                    NullValueHandling = NullValueHandling.Ignore,
                });
                if (settings == null)
                {
                    Debug.LogWarning($"[TrainingSettings] {SettingsFileName} parsed to null; using baked defaults.");
                    return new TrainingSettings();
                }
                Debug.Log($"[TrainingSettings] Loaded from {_settingsPath} (schemaVersion={settings.SchemaVersion}).");
                WarnOnFrozenObservationDivergence(settings);
                return settings;
            }
            catch (JsonException ex)
            {
                Debug.LogError($"[TrainingSettings] Parse error in {SettingsFileName}: {ex.Message}; using baked defaults.");
                return new TrainingSettings();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[TrainingSettings] Unexpected error loading {SettingsFileName}: {ex.Message}; using baked defaults.");
                return new TrainingSettings();
            }
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
