using UnityEngine;

namespace UnityPpoRacingTrainer.Core.AiDriver.Internal
{
    /// <summary>
    /// <c>Resources.Load&lt;T&gt;</c> wrapper that works around a Unity 6 quirk
    /// where assets shipped inside a git-URL-fetched package's
    /// <c>Resources/</c> folder are not always registered in the
    /// <c>AssetDatabase</c>. Symptoms in consumer projects: every
    /// <c>Resources.Load&lt;T&gt;("AiDriver/...")</c> returns null even though
    /// the files are physically present in
    /// <c>Library/PackageCache/com.sergiolinarescodes.ppo-racing-trainer@&lt;hash&gt;/Runtime/Resources/</c>.
    ///
    /// In Editor builds the fallback path forces an <c>AssetDatabase.ImportAsset</c>
    /// on the known package path then re-tries <c>LoadAssetAtPath</c>. In
    /// player builds we trust <c>Resources.Load</c> — the build pipeline
    /// resolves Resources/ at scan time, which is a different code path
    /// from the Editor AssetDatabase and does not suffer the quirk.
    /// </summary>
    public static class PackageResourceLoader
    {
        // Mirrors the package id in package.json. Update both together.
        private const string PackageResourcesRoot =
            "Packages/com.sergiolinarescodes.ppo-racing-trainer/Runtime/Resources";

        /// <summary>
        /// <paramref name="resourcePath"/> is the same string you'd pass to
        /// <c>Resources.Load</c> (no extension). <paramref name="assetExtension"/>
        /// is the on-disk extension including the leading dot (e.g. ".prefab",
        /// ".onnx").
        /// </summary>
        public static T Load<T>(string resourcePath, string assetExtension) where T : Object
        {
            var asset = Resources.Load<T>(resourcePath);
            if (asset != null) return asset;

#if UNITY_EDITOR
            string packagePath = $"{PackageResourcesRoot}/{resourcePath}{assetExtension}";
            asset = UnityEditor.AssetDatabase.LoadAssetAtPath<T>(packagePath);
            if (asset != null) return asset;

            // Last-resort: force the importer to register, then retry.
            // ImportAsset on a Packages/... path is supported even though
            // package contents are immutable — only the import index is
            // being touched here.
            try { UnityEditor.AssetDatabase.ImportAsset(packagePath); }
            catch { /* path missing or unimportable — fall through */ }
            asset = UnityEditor.AssetDatabase.LoadAssetAtPath<T>(packagePath);
#endif
            return asset;
        }
    }
}
