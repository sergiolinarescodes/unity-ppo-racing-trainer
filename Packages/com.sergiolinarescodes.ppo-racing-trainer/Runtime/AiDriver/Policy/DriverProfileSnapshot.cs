using UnityPpoRacingTrainer.Core.AiDriver.Physics;

namespace UnityPpoRacingTrainer.Core.AiDriver.Policy
{
    /// <summary>
    /// Six-axis driver-personality vector consumed by the trained policy's
    /// observation block and by reward / physics modifiers (fuel burn, tire
    /// wear, car-car contact damage). Two reserved slots leave room to ship
    /// new dimensions (e.g. wet-weather skill) without breaking the existing
    /// observation schema.
    /// </summary>
    /// <param name="TirePreservation">
    /// 0..1. UP: smoother inputs, longer tire life, slower lap.
    /// DOWN: hot tire wear accepted for early pace.
    /// </param>
    /// <param name="FuelEconomy">
    /// 0..1. UP: lift-and-coast — slower lap, more laps per tank.
    /// DOWN: full throttle held — fastest pace, fuel burns fast.
    /// </param>
    /// <param name="PassingAggression">
    /// 0..1. UP: commits to half-gap overtakes, accepts contact.
    /// DOWN: waits for clear opportunities.
    /// </param>
    /// <param name="DefendingResolve">
    /// 0..1. UP: blocks inside lines and holds the racing line.
    /// DOWN: yields the apex easily.
    /// </param>
    /// <param name="RiskTolerance">
    /// 0..1. UP: tight wall margins and late braking — high upside, high crash rate.
    /// DOWN: conservative margins everywhere.
    /// </param>
    /// <param name="PeakPaceBias">
    /// 0..1. UP: qualifying-style one-lap push.
    /// DOWN: stint-style race-distance pacing.
    /// </param>
    /// <param name="Reserved0">Reserved for future dimension. Always 0 today.</param>
    /// <param name="Reserved1">Reserved for future dimension. Always 0 today.</param>
    public readonly record struct DriverPersonality(
        float TirePreservation,
        float FuelEconomy,
        float PassingAggression,
        float DefendingResolve,
        float RiskTolerance,
        float PeakPaceBias,
        float Reserved0,
        float Reserved1)
    {
        public const int ObservationLength = 8;

        /// <summary>Neutral 0.5-everywhere baseline. No bias on any axis.</summary>
        public static DriverPersonality Default => new(
            TirePreservation: 0.5f,
            FuelEconomy: 0.5f,
            PassingAggression: 0.5f,
            DefendingResolve: 0.5f,
            RiskTolerance: 0.5f,
            PeakPaceBias: 0.5f,
            Reserved0: 0f,
            Reserved1: 0f);

        /// <summary>Maximises tire life; conservative inputs, lower peak pace.</summary>
        public static DriverPersonality TirePreserver => new(
            TirePreservation: 0.9f, FuelEconomy: 0.6f, PassingAggression: 0.3f,
            DefendingResolve: 0.5f, RiskTolerance: 0.2f, PeakPaceBias: 0.4f,
            Reserved0: 0f, Reserved1: 0f);

        /// <summary>Squeezes maximum laps per tank; coasts into braking zones.</summary>
        public static DriverPersonality FuelSaver => new(
            TirePreservation: 0.6f, FuelEconomy: 0.95f, PassingAggression: 0.3f,
            DefendingResolve: 0.4f, RiskTolerance: 0.2f, PeakPaceBias: 0.3f,
            Reserved0: 0f, Reserved1: 0f);

        /// <summary>Overtake-first archetype. High risk, high incident rate.</summary>
        public static DriverPersonality Attacker => new(
            TirePreservation: 0.3f, FuelEconomy: 0.3f, PassingAggression: 0.95f,
            DefendingResolve: 0.4f, RiskTolerance: 0.85f, PeakPaceBias: 0.9f,
            Reserved0: 0f, Reserved1: 0f);

        /// <summary>Holds position; blocks inside lines and keeps the apex.</summary>
        public static DriverPersonality Defender => new(
            TirePreservation: 0.6f, FuelEconomy: 0.5f, PassingAggression: 0.4f,
            DefendingResolve: 0.95f, RiskTolerance: 0.5f, PeakPaceBias: 0.6f,
            Reserved0: 0f, Reserved1: 0f);

        /// <summary>Balanced baseline; no dimension extreme either way.</summary>
        public static DriverPersonality AllRounder => new(
            TirePreservation: 0.6f, FuelEconomy: 0.6f, PassingAggression: 0.6f,
            DefendingResolve: 0.6f, RiskTolerance: 0.5f, PeakPaceBias: 0.6f,
            Reserved0: 0f, Reserved1: 0f);

        /// <summary>Maximum-risk archetype. Burns tires and fuel chasing peak pace.</summary>
        public static DriverPersonality RiskTaker => new(
            TirePreservation: 0.2f, FuelEconomy: 0.3f, PassingAggression: 0.85f,
            DefendingResolve: 0.5f, RiskTolerance: 0.95f, PeakPaceBias: 0.95f,
            Reserved0: 0f, Reserved1: 0f);

        public void WriteTo(System.Span<float> dest)
        {
            dest[0] = TirePreservation;
            dest[1] = FuelEconomy;
            dest[2] = PassingAggression;
            dest[3] = DefendingResolve;
            dest[4] = RiskTolerance;
            dest[5] = PeakPaceBias;
            dest[6] = Reserved0;
            dest[7] = Reserved1;
        }
    }

    /// <summary>
    /// Frozen runtime view of a <see cref="DriverProfile"/>. Built once per
    /// agent at episode start so the policy code can read style + car config
    /// without ever mutating the source ScriptableObject. <see cref="Aggression"/>
    /// and <see cref="Caution"/> drive observation features and high-level
    /// reward shaping; <see cref="Personality"/> carries the policy-conditioning
    /// vector that lets the trained network behave as a specific archetype.
    /// </summary>
    /// <param name="Id">Profile identifier (matches <see cref="DriverProfile.ProfileId"/>).</param>
    /// <param name="Aggression">
    /// 0..1 dial. UP: late braking, attempted overtakes, higher incident rate.
    /// DOWN: defensive driving, cleaner laps.
    /// </param>
    /// <param name="Caution">
    /// 0..1 dial. UP: bigger margins to walls and traffic — lower wreck rate.
    /// DOWN: tight margins — faster but riskier.
    /// </param>
    /// <param name="Car">Per-driver car parameters (either an override or the default).</param>
    /// <param name="Personality">Six-axis personality vector — see <see cref="DriverPersonality"/>.</param>
    public readonly record struct DriverProfileSnapshot(
        string Id,
        float Aggression,
        float Caution,
        CarParameters Car,
        DriverPersonality Personality)
    {
        public static DriverProfileSnapshot Default => new(
            Id: "default",
            Aggression: 0.5f,
            Caution: 0.5f,
            Car: AiDriverPhysicsDefaults.Latest,
            Personality: DriverPersonality.Default);
    }
}
