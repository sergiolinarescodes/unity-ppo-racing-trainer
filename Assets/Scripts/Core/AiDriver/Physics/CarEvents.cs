using UnityPpoRacingTrainer.Core.Track;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.AiDriver.Physics
{
    public readonly record struct CarSpawnedEvent(CarId Id, Vector3 Position, float Heading);

    public readonly record struct CarDespawnedEvent(CarId Id);

    /// <summary>
    /// Emitted once per fixed tick per active car. Carries enough state to drive
    /// debug overlays + episode recorders without forcing a state lookup.
    /// </summary>
    public readonly record struct CarStateUpdatedEvent(
        CarId Id,
        Vector3 Position,
        float Heading,
        float Speed,
        bool OnGround);

    public readonly record struct CarOffTrackEvent(CarId Id);

    public readonly record struct CarBackOnTrackEvent(CarId Id);

    public readonly record struct CarLapCompletedEvent(CarId Id, int LapNumber, float LapTimeSeconds);

    /// <summary>Car penetrated a wall this tick. Position has been resolved + velocity damped.</summary>
    public readonly record struct CarHitWallEvent(CarId Id, Vector3 Position, Vector3 Normal, float ImpactSpeed);

    /// <summary>Car's projection just entered a kerb zone.</summary>
    public readonly record struct CarOnKerbEvent(CarId Id);

    /// <summary>Car's projection just left a kerb zone.</summary>
    public readonly record struct CarOffKerbEvent(CarId Id);

    /// <summary>
    /// Richer per-tick state for side-systems (tire wear, fuel burn, drafting,
    /// puncture detection). Carries the values computed inside the integrator
    /// that the lean <see cref="CarStateUpdatedEvent"/> intentionally omits.
    /// </summary>
    /// <param name="Id">Car identifier.</param>
    /// <param name="Position">World position at the end of this tick.</param>
    /// <param name="Heading">Heading in radians (0 = +Z, CCW positive).</param>
    /// <param name="Speed">Forward speed magnitude, m/s.</param>
    /// <param name="LateralAcceleration">
    /// Centripetal acceleration estimate, m/s², signed by turn direction.
    /// Positive = right-hand curve (loads left tires).
    /// </param>
    /// <param name="Slip">|lateralVelocity| / speed, clamped to [0, 1]. 0 = pure forward, 1 = full sideways.</param>
    /// <param name="ThrottleInput">Throttle component this tick, [0, 1].</param>
    /// <param name="BrakeInput">Brake component this tick, [0, 1].</param>
    /// <param name="Surface">Surface kind the projection landed on (asphalt / kerb / off-track).</param>
    /// <param name="ArcLengthAlong">Arc length along the loop, world units (lap distance).</param>
    /// <param name="Dt">Tick duration in seconds (fixed-step dt).</param>
    public readonly record struct CarPhysicsTickedEvent(
        CarId Id,
        UnityEngine.Vector3 Position,
        float Heading,
        float Speed,
        float LateralAcceleration,
        float Slip,
        float ThrottleInput,
        float BrakeInput,
        SurfaceKind Surface,
        float ArcLengthAlong,
        float Dt);

    /// <summary>Car-vs-car contact resolved this tick.</summary>
    public readonly record struct CarHitCarEvent(
        CarId A,
        CarId B,
        UnityEngine.Vector3 Position,
        UnityEngine.Vector3 Normal,
        float ImpactSpeed);

    /// <summary>One of a car's wheels punctured (wear hit critical at high G).</summary>
    public enum TireSide : byte { Left = 0, Right = 1 }

    public readonly record struct TirePuncturedEvent(CarId Id, TireSide Side, float WearAtPuncture);

    /// <summary>Continuous draft strength (0..1) toward a leading car, smoothed for slingshot.</summary>
    public readonly record struct DraftStateChangedEvent(CarId Id, CarId? LeaderId, float Strength);

    /// <summary>Car ran out of fuel this tick. Sim now forces coast.</summary>
    public readonly record struct FuelDepletedEvent(CarId Id);

    /// <summary>Position swap detected by the race-state service.</summary>
    public readonly record struct OvertakeEvent(CarId Passer, CarId Passed, int NewPosition);

    /// <summary>
    /// Published when a per-episode personality is sampled by the reward
    /// shaper. The policy service subscribes and updates its cached agent
    /// record so the next <c>CollectObservations</c> writes the new vector;
    /// without this update the policy would never see the randomized
    /// conditioning and personality-driven behaviour would not emerge.
    /// </summary>
    public readonly record struct DriverPersonalityChangedEvent(
        CarId Id,
        Policy.DriverPersonality Personality);
}
