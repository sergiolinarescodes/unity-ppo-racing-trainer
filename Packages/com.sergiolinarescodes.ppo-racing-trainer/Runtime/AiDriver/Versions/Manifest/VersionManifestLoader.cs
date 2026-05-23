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
    /// Two sources, merged with disk-wins precedence:
    /// <list type="number">
    /// <item>Disk: <c>Assets/_Bootstrap/Configs/Versions/*.json</c> in the
    /// host Unity project. Trainer's live-edit path — the /settings UI
    /// writes here.</item>
    /// <item>Resources fallback: <c>Resources/AiDriver/Versions/*.json</c>
    /// shipped inside the UPM package. Consumers (e.g. the game) get the
    /// canonical manifests baked into the package without having to copy
    /// training config files into their own repo.</item>
    /// </list>
    /// </summary>
    public static class VersionManifestLoader
    {
        public const string DefaultRelativeFolder = "Assets/_Bootstrap/Configs/Versions";
        public const string DefaultResourcesPath = "AiDriver/Versions";

        // Per-name fallback list. Resources.LoadAll on a folder inside a
        // package's Resources/ tree is unreliable when the package is fetched
        // as a git URL (Unity imports the TextAssets but LoadAll on the
        // subfolder returns empty). Direct Resources.Load by name always
        // works once the asset is imported. Add any new id shipped under
        // Runtime/Resources/AiDriver/Versions/ here so it survives the
        // LoadAll quirk on consumer projects.
        private static readonly string[] PackagedVersionIds = { "latest", "v1" };

        public static IReadOnlyDictionary<string, VersionManifest> LoadAll(string folder = null)
        {
            var result = new Dictionary<string, VersionManifest>(StringComparer.OrdinalIgnoreCase);

            // Pass 1 — disk. Trainer project edits these via the /settings UI.
            var dir = ResolveFolder(folder ?? DefaultRelativeFolder);
            if (Directory.Exists(dir))
            {
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
            }
            else
            {
                Debug.Log($"[VersionManifestLoader] disk folder missing at {dir}; trying package-shipped Resources.");
            }

            // Pass 2 — Resources.LoadAll on the package's Resources subfolder.
            // Auto-picks up anything dropped into Runtime/Resources/AiDriver/
            // Versions/. Disk entries above always win.
            int beforeResources = result.Count;
            var assets = Resources.LoadAll<TextAsset>(DefaultResourcesPath);
            int pickedLoadAll = 0;
            foreach (var ta in assets)
            {
                if (ta == null || string.IsNullOrEmpty(ta.text)) continue;
                var manifest = TryParse(ta.text, $"Resources/{DefaultResourcesPath}/{ta.name}.json");
                if (manifest == null) continue;
                if (result.ContainsKey(manifest.VersionId)) continue;
                result[manifest.VersionId] = manifest;
                pickedLoadAll++;
            }
            Debug.Log($"[VersionManifestLoader] Resources.LoadAll returned {assets.Length} TextAsset(s), kept {pickedLoadAll}.");

            // Pass 3 — per-name fallback. Resources.LoadAll on a subfolder of
            // a package-shipped Resources/ tree returns an empty array in
            // some Unity 6 configurations even though TextScriptImporter
            // successfully imported the files. Direct Resources.Load by name
            // works in every case once the asset is imported.
            int pickedByName = 0;
            foreach (var id in PackagedVersionIds)
            {
                if (result.ContainsKey(id)) continue;
                var ta = Resources.Load<TextAsset>($"{DefaultResourcesPath}/{id}");
                if (ta == null || string.IsNullOrEmpty(ta.text))
                {
                    Debug.LogWarning($"[VersionManifestLoader] direct Resources.Load failed for {DefaultResourcesPath}/{id}.json — package may not be imported yet.");
                    continue;
                }
                var manifest = TryParse(ta.text, $"Resources/{DefaultResourcesPath}/{id}.json");
                if (manifest == null) continue;
                if (result.ContainsKey(manifest.VersionId)) continue;
                result[manifest.VersionId] = manifest;
                pickedByName++;
            }
            if (pickedByName > 0)
            {
                Debug.Log($"[VersionManifestLoader] by-name fallback picked up {pickedByName} additional manifest(s).");
            }

            Debug.Log($"[VersionManifestLoader] final manifest count = {result.Count} (disk had {beforeResources}).");
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
            return TryParse(json, path);
        }

        private static VersionManifest TryParse(string json, string sourceLabel)
        {
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
                Debug.LogError($"[VersionManifestLoader] parse error at {sourceLabel}: {ex.Message}");
                return null;
            }

            if (manifest == null)
            {
                Debug.LogError($"[VersionManifestLoader] parsed to null: {sourceLabel}");
                return null;
            }
            if (manifest.SchemaVersion != 1)
            {
                Debug.LogError($"[VersionManifestLoader] schema_version {manifest.SchemaVersion} not supported at {sourceLabel}; skipped.");
                return null;
            }
            if (string.IsNullOrWhiteSpace(manifest.VersionId))
            {
                Debug.LogError($"[VersionManifestLoader] version_id missing at {sourceLabel}; skipped.");
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
