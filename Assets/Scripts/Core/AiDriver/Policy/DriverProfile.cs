using UnityPpoRacingTrainer.Core.AiDriver.Physics;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.AiDriver.Policy
{
    /// <summary>
    /// Authored per-driver style + tuning ScriptableObject. Lives under
    /// <c>Assets/Resources/AiDriver/Profiles/</c> so <see cref="DriverProfileRegistry"/>
    /// can lazy-load any profile by id without a scene reference. A frozen
    /// runtime view is taken via <see cref="ToSnapshot"/> at episode start so
    /// the SO is never mutated by gameplay code.
    /// </summary>
    [CreateAssetMenu(fileName = "DriverProfile", menuName = "RACING/AI Driver/Driver Profile", order = 200)]
    public sealed class DriverProfile : ScriptableObject
    {
        /// <summary>
        /// Stable string identifier. Used as the lookup key by
        /// <see cref="DriverProfileRegistry"/> and as the ML-Agents agent name.
        /// </summary>
        [SerializeField] private string profileId = "default";

        /// <summary>
        /// Coarse "how aggressive is this driver" dial, 0..1. Surfaced into the
        /// policy observation vector.
        /// UP: agent biases toward late braking, attempted overtakes, tighter
        /// kerb usage — higher incident rate.
        /// DOWN: defensive driving, earlier braking, wider lines — cleaner laps
        /// but lower top pace.
        /// </summary>
        [SerializeField, Range(0f, 1f)] private float aggression = 0.5f;

        /// <summary>
        /// Coarse "how cautious is this driver" dial, 0..1. Mirror of
        /// <see cref="aggression"/>, surfaced separately so the policy can
        /// learn non-symmetric behaviour (e.g. aggressive AND cautious = clean
        /// but fast specialist).
        /// UP: more margin near walls and other cars; lower wreck rate, lower
        /// peak pace.
        /// DOWN: tighter margins, faster on raw timing, higher crash risk.
        /// </summary>
        [SerializeField, Range(0f, 1f)] private float caution = 0.5f;

        /// <summary>
        /// When true, <see cref="carParameters"/> below is used instead of the
        /// default car config. Allows per-driver mass / brake / grip overrides
        /// (e.g. a "heavy chassis" archetype) without forking <see cref="AiDriverPhysicsDefaults.Latest"/>.
        /// </summary>
        [SerializeField] private bool overrideCarParameters = false;

        /// <summary>
        /// Optional per-driver car-parameter override, used only when
        /// <see cref="overrideCarParameters"/> is true.
        /// </summary>
        [SerializeField] private CarParameters carParameters = AiDriverPhysicsDefaults.Latest;

        [Header("Personality (policy conditioning)")]

        /// <summary>
        /// How much the driver wants to preserve tire life, 0..1.
        /// UP: smoother throttle/brake application, gentler kerb usage — tires
        /// last longer at the cost of single-lap pace.
        /// DOWN: hot tire wear is acceptable — peak pace early, falling off
        /// late in the stint.
        /// </summary>
        [SerializeField, Range(0f, 1f)] private float tirePreservation = 0.5f;

        /// <summary>
        /// How much the driver values fuel economy, 0..1.
        /// UP: lift-and-coast behaviour into braking zones — slower lap but
        /// more laps per tank, lower DNF-from-empty risk.
        /// DOWN: throttle held to the limiter — maximum pace but burns fuel
        /// fast.
        /// </summary>
        [SerializeField, Range(0f, 1f)] private float fuelEconomy = 0.5f;

        /// <summary>
        /// Willingness to commit to overtakes, 0..1.
        /// UP: dives into half-gaps, brakes later beside opponents, accepts
        /// contact — more passes attempted, more incidents.
        /// DOWN: waits for the next lap or a clear line — calmer but slower
        /// to progress through traffic.
        /// </summary>
        [SerializeField, Range(0f, 1f)] private float passingAggression = 0.5f;

        /// <summary>
        /// Willingness to defend a position, 0..1.
        /// UP: stays on the racing line, blocks inside lines on entry — fewer
        /// passes succeed against this driver, more side-by-side contact.
        /// DOWN: gives the apex away to incoming traffic — cleaner but loses
        /// positions easily.
        /// </summary>
        [SerializeField, Range(0f, 1f)] private float defendingResolve = 0.5f;

        /// <summary>
        /// Overall appetite for risk, 0..1.
        /// UP: tighter margins to walls and other cars, late braking, willing
        /// to slide — high upside, high crash rate.
        /// DOWN: conservative everywhere — predictable, low variance.
        /// </summary>
        [SerializeField, Range(0f, 1f)] private float riskTolerance = 0.5f;

        /// <summary>
        /// Bias toward extracting peak single-lap pace versus race-distance
        /// pace, 0..1.
        /// UP: qualifying-style driver — pushes for one perfect lap, less
        /// energy management.
        /// DOWN: stint-style driver — manages pace over many laps, less peak
        /// raw speed.
        /// </summary>
        [SerializeField, Range(0f, 1f)] private float peakPaceBias = 0.5f;

        public string ProfileId => profileId;
        public float Aggression => aggression;
        public float Caution => caution;

        public DriverProfileSnapshot ToSnapshot() => new(
            Id: string.IsNullOrEmpty(profileId) ? name : profileId,
            Aggression: aggression,
            Caution: caution,
            Car: overrideCarParameters ? carParameters : AiDriverPhysicsDefaults.Latest,
            Personality: new DriverPersonality(
                TirePreservation: tirePreservation,
                FuelEconomy: fuelEconomy,
                PassingAggression: passingAggression,
                DefendingResolve: defendingResolve,
                RiskTolerance: riskTolerance,
                PeakPaceBias: peakPaceBias,
                Reserved0: 0f,
                Reserved1: 0f));
    }
}
