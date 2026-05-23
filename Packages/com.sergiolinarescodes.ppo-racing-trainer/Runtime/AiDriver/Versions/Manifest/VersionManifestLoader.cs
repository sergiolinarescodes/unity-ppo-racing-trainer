using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.AiDriver.Versions.Manifest
{
    /// <summary>
    /// Reads every <c>*.json</c> file under <c>Assets/_Bootstrap/Configs/Versions/</c>
    /// and returns a dictionary keyed by <see cref="VersionManifest.VersionId"/>.
    /// Tolerates missing folder, malformed JSON, and schema-version drift —
    /// callers get an empty dictionary or fewer-than-expected entries, never
    /// an exception. Mirrors the tolerant-load discipline already used by
    /// <c>TrainingSettingsService</c>.
    ///
    /// Consumer projects (e.g. a game using the package as a UPM dep) typically
    /// have no Versions folder of their own. They skip
    /// <c>VersionManifestSystemInstaller</c> entirely and let
    /// <c>AiDriverVersionsSystemInstaller</c> register the built-in default
    /// profile instead.
    /// </summary>
    public static class VersionManifestLoader
    {
        public const string DefaultRelativeFolder = "Assets/_Bootstrap/Configs/Versions";

        public static IReadOnlyDictionary<string, VersionManifest> LoadAll(string folder = null)
        {
            var dir = ResolveFolder(folder ?? DefaultRelativeFolder);
            var result = new Dictionary<string, VersionManifest>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(dir))
            {
                Debug.Log($"[VersionManifestLoader] folder missing at {dir}; returning empty set.");
                return result;
            }

            foreach (var path in Directory.GetFiles(dir, "*.json"))
            {
                var manifest = TryLoad(path);
                if (manifest == null) continue;
                if (result.ContainsKey(manifest.VersionId))
                {
                    Debug.LogError($"[VersionManifestLoader] duplicate version_id '{manifest.VersionId}' at {path}; ignored.");
                    continue;
                }
                result[manifest.VersionId] = manifest;
            }

            Debug.Log($"[VersionManifestLoader] loaded {result.Count} manifest(s) from {dir}.");
            return result;
        }

        public static VersionManifest TryLoad(string path)
        {
            string json;
            try
            {
                json = File.ReadAllText(path);
            }
            catch (IOException ex)
            {
                Debug.LogError($"[VersionManifestLoader] read failed at {path}: {ex.Message}");
                return null;
            }

            VersionManifest manifest;
            try
            {
                manifest = JsonConvert.DeserializeObject<VersionManifest>(json, new JsonSerializerSettings
                {
                    MissingMemberHandling = MissingMemberHandling.Ignore,
                    NullValueHandling = NullValueHandling.Ignore,
                });
            }
            catch (JsonException ex)
            {
                Debug.LogError($"[VersionManifestLoader] parse error at {path}: {ex.Message}");
                return null;
            }

            if (manifest == null)
            {
                Debug.LogError($"[VersionManifestLoader] parsed to null: {path}");
                return null;
            }
            if (manifest.SchemaVersion != 1)
            {
                Debug.LogError($"[VersionManifestLoader] schema_version {manifest.SchemaVersion} not supported at {path}; skipped.");
                return null;
            }
            if (string.IsNullOrWhiteSpace(manifest.VersionId))
            {
                Debug.LogError($"[VersionManifestLoader] version_id missing at {path}; skipped.");
                return null;
            }
            return manifest;
        }

        private static string ResolveFolder(string folder)
        {
            if (Path.IsPathRooted(folder)) return folder;
            // Application.dataPath ends with .../Assets; project root is one up.
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", folder));
        }
    }
}
