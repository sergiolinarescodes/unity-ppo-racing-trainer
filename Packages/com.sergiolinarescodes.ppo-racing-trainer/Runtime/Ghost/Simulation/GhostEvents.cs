using UnityPpoRacingTrainer.Core.AiDriver.Physics;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Ghost.Simulation
{
    public enum GhostSpawnCause { InitialSpawn, LapCompleted, OffTrack, TrackChanged }

    public readonly record struct GhostLapCompletedEvent(CarId GhostId, int LapNumber, float LapTimeSeconds);
    public readonly record struct GhostOffTrackEvent(CarId GhostId, Vector3 ExitPos);
    public readonly record struct GhostSpawnRequestedEvent(Vector3 LandPos, float Heading, GhostSpawnCause Cause);
    public readonly record struct GhostLandedEvent(CarId GhostId);
    public readonly record struct GhostSettledEvent(CarId GhostId);
    public readonly record struct GhostDrivingStartedEvent(CarId GhostId);
    public readonly record struct GhostRespawnedEvent(CarId GhostId, GhostSpawnCause Cause);

    /// <summary>
    /// Per-tick snapshot of ghost simulation state. Consumers (presenter,
    /// director, scenario logs) read this — never the car's transform.
    /// </summary>
    public readonly record struct GhostSimSnapshot(
        Vector3 Position,
        float Heading,
        float Speed,
        bool IsOffTrack,
        int LapsCompleted,
        float BodyLeanRad);
}
