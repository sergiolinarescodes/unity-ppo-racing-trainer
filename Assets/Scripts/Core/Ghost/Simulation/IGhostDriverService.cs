using UnityPpoRacingTrainer.Core.AiDriver.Physics;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Ghost.Simulation
{
    /// <summary>
    /// Spawns + drives the singleton ghost car. The director toggles drive enable
    /// during SpawnDrop / Settle (off) → Drive (on) → Respawn (off). Reads sim
    /// state via <see cref="ICarSimulationService.TryGetState"/> and writes
    /// inputs via <see cref="ICarSimulationService.SetInput"/>; never touches the
    /// car's transform.
    /// </summary>
    public interface IGhostDriverService
    {
        CarId GhostId { get; }
        bool HasSpawned { get; }
        bool DriveEnabled { get; set; }

        void Spawn(Vector3 worldPos, float heading);
        void Teleport(Vector3 worldPos, float heading);
        void Despawn();

        bool TryReadSnapshot(out GhostSimSnapshot snapshot);
    }
}
