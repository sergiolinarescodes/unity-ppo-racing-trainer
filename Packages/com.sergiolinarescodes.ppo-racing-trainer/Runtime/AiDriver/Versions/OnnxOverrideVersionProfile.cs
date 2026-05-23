using UnityPpoRacingTrainer.Core.AiDriver.Physics;
using UnityPpoRacingTrainer.Core.AiDriver.Training;
using UnityPpoRacingTrainer.Core.AiDriver.Versions.Manifest;

namespace UnityPpoRacingTrainer.Core.AiDriver.Versions
{
    /// <summary>
    /// Decorator that wraps an existing <see cref="IAiDriverVersionProfile"/>
    /// and swaps only the <c>VersionId</c> + <c>OnnxResourcePath</c>. Used by
    /// the by-name ONNX fallback in <c>AiDriverVersionsSystemInstaller</c>:
    /// when <c>activeVersionId</c> doesn't match any manifest but matches an
    /// ONNX file under <c>Resources/AiDriver/Policies/</c>, the active profile
    /// is the "latest" manifest with the model swapped out — so you can spot-
    /// check a new ONNX without authoring a manifest first.
    ///
    /// Caveat: physics, reward, drafting, and observation layout still come
    /// from the inner profile (i.e. "latest"). If the new ONNX was trained on
    /// a different observation shape, the inference will be silently wrong —
    /// promote to a real manifest before you trust the numbers.
    /// </summary>
    internal sealed class OnnxOverrideVersionProfile : IAiDriverVersionProfile
    {
        private readonly IAiDriverVersionProfile _inner;
        private readonly string _versionId;
        private readonly string _onnxResourcePath;

        public OnnxOverrideVersionProfile(IAiDriverVersionProfile inner, string versionId, string onnxResourcePath)
        {
            _inner = inner;
            _versionId = versionId;
            _onnxResourcePath = onnxResourcePath;
        }

        public string VersionId => _versionId;
        public string BehaviorName => _inner.BehaviorName;
        public string PrefabResourcePath => _inner.PrefabResourcePath;
        public string OnnxResourcePath => _onnxResourcePath;
        public string YamlConfigPath => _inner.YamlConfigPath;
        public int FloatsPerFrame => _inner.FloatsPerFrame;
        public CarParameters PhysicsDefaults => _inner.PhysicsDefaults;
        public DraftingSettings Drafting => _inner.Drafting;
        public IRewardShaper RewardShaper => _inner.RewardShaper;
        public bool RequiresSideSystems => _inner.RequiresSideSystems;
        public VersionManifest Manifest => _inner.Manifest;
    }
}
