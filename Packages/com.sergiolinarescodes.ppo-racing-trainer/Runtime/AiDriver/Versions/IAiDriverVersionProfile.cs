using UnityPpoRacingTrainer.Core.AiDriver.Physics;
using UnityPpoRacingTrainer.Core.AiDriver.Training;
using UnityPpoRacingTrainer.Core.AiDriver.Versions.Manifest;

namespace UnityPpoRacingTrainer.Core.AiDriver.Versions
{

    /// <summary>
    /// Immutable bundle that captures everything a "premium AI driver model
    /// version" depends on: the BehaviorName ML-Agents Python trainer key,
    /// the Resources prefab + ONNX paths, the float-count schema, the frozen
    /// physics tunings, the reward shaper, and whether the side-system stack
    /// (tires, fuel, draft, car-car collision, race state) is wired up.
    ///
    /// Concrete profiles are <c>internal sealed</c> per the project's
    /// <c>SystemServiceBase</c>-style convention. Resolve the active profile
    /// through DI — <c>container.Resolve&lt;IAiDriverVersionProfile&gt;()</c>
    /// returns the one keyed to the bootstrap's <c>activeVersion</c> field.
    /// </summary>
    public interface IAiDriverVersionProfile
    {
        /// <summary>Stable id of the version — matches the manifest's
        /// <c>versionId</c> field and the manifest filename
        /// (<c>"latest"</c>, <c>"v1"</c>, …).</summary>
        string VersionId { get; }

        /// <summary>ML-Agents Python trainer key. Must match the yaml's
        /// <c>behaviors:</c> top-level entry and the prefab's
        /// <c>BehaviorParameters.BehaviorName</c> verbatim.</summary>
        string BehaviorName { get; }

        /// <summary>Path passed to <c>Resources.Load&lt;GameObject&gt;</c> when
        /// the trainer / test scene spawns this version's agent.</summary>
        string PrefabResourcePath { get; }

        /// <summary>Path to the ONNX policy file under <c>Resources/</c> (no
        /// extension). Informational — ML-Agents loads ONNX through the
        /// BehaviorParameters' <c>Model</c> field, but tooling and the
        /// <c>test-circuit-with-model</c> skill consult this string.
        ///
        /// The sentinel value <c>"latest"</c> means "resolve at call time to
        /// the newest-mtime <c>RacingDriver-*.onnx</c> under
        /// <c>Assets/Resources/AiDriver/Policies/</c>" — used by the canonical
        /// Latest profile so the skill doesn't need to bump this string after
        /// every model swap. The supervisor-overwritten baseline
        /// <c>RacingDriver.onnx</c> (no tag suffix) is excluded from the
        /// scan.</summary>
        string OnnxResourcePath { get; }

        /// <summary>Project-relative path to the trainer yaml for this version.
        /// The Python supervisor reads this to find the ML-Agents config.</summary>
        string YamlConfigPath { get; }

        /// <summary>Total observation floats per decision tick. Must match
        /// <c>BehaviorParameters.VectorObservationSize</c> on the prefab.</summary>
        int FloatsPerFrame { get; }

        /// <summary>Starting physics for this version, frozen at the time the
        /// snapshot was taken. Side-system modifiers (tire wear, fuel mass,
        /// draft drag) still mutate per-car copies of these values at
        /// runtime; this property exposes the unmodified baseline.</summary>
        CarParameters PhysicsDefaults { get; }

        /// <summary>Slipstream / drafting constants — wake distance, lateral
        /// tolerance, drag cut, catch-up accel boost, activation thresholds,
        /// and attack/release smoothing taus. The <c>DraftService</c> consumes
        /// this in its ctor instead of baking the constants; the values come
        /// from the active version's manifest <c>drafting</c> section.</summary>
        DraftingSettings Drafting { get; }

        /// <summary>Reward shaper to plug into the policy service. Snapshots
        /// trained without shaping return a no-op shaper; Latest uses the
        /// canonical <see cref="RewardShaper"/>.</summary>
        IRewardShaper RewardShaper { get; }

        /// <summary>True when the agent expects the tire / fuel / draft /
        /// car-car-collision / race-state installers to be wired up.
        /// Snapshots trained with a stripped-down environment leave this
        /// false so the side-systems stay dormant; Latest is true.</summary>
        bool RequiresSideSystems { get; }

        /// <summary>
        /// Raw manifest backing this profile. Exposed so extension installers
        /// (reward channels, observation writer, physics model) can read the
        /// version's <c>CodeModules</c> and <c>RewardChannels</c> entries
        /// without re-loading the JSON. Every profile is manifest-backed since
        /// Phase 4.
        /// </summary>
        VersionManifest Manifest { get; }
    }
}
