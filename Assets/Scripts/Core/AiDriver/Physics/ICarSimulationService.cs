using System.Collections.Generic;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.AiDriver.Physics
{
    /// <summary>
    /// Headless car simulation. Pure math — no Rigidbody, no PhysX. Cars are
    /// integer-IDed entries; visuals are a separate concern handled by the
    /// evaluation system. Tick is fixed timestep so RL training is deterministic.
    /// </summary>
    public interface ICarSimulationService
    {
        IReadOnlyCollection<CarId> ActiveCars { get; }

        CarId Spawn(Vector3 position, float heading, CarParameters parameters);

        /// <summary>
        /// Re-create an entry for an existing <paramref name="id"/> that was
        /// previously despawned (e.g. eliminated in race-scoped mode). Used
        /// by the policy service when ML-Agents reopens an episode for a car
        /// that's no longer in <see cref="ActiveCars"/> — a plain
        /// <see cref="TeleportTo"/> would no-op because the entry's gone.
        /// Publishes <c>CarSpawnedEvent</c> so the race coordinator transitions
        /// out of <c>Ended</c> and opens a fresh race window.
        /// </summary>
        void RespawnExisting(CarId id, Vector3 position, float heading, CarParameters parameters);

        void Despawn(CarId id);

        void SetInput(CarId id, DriverInput input);

        bool TryGetState(CarId id, out CarState state);

        /// <summary>Reset position/heading and zero velocities — for episode reset.</summary>
        void TeleportTo(CarId id, Vector3 position, float heading);

        /// <summary>
        /// Current chassis health in [0, 1]. 1 = pristine, 0 = wrecked. Damage
        /// accumulates from wall impacts (∝ impact speed²); the speed cap scales
        /// with health so a damaged car is also a slower car. Returns false if
        /// the car id is unknown.
        /// </summary>
        bool TryGetHealth(CarId id, out float health);

        /// <summary>
        /// Add a velocity delta (XZ) to the car this tick — used by the car-car
        /// collision service to apply pairwise impulses outside the integrator's
        /// per-car loop. Idempotent if the car id is unknown.
        /// </summary>
        void ApplyImpulse(CarId id, Vector2 deltaVelocityXZ);

        /// <summary>
        /// Translate the car's XZ position by <paramref name="worldOffsetXZ"/>
        /// without touching velocity. Used by the car-car collision resolver to
        /// push two overlapping cars apart along the separation normal. This
        /// path deliberately bypasses dynamics — feeding a positional
        /// penetration depth through <see cref="ApplyImpulse"/> would inject
        /// metres into a m/s state vector and compound every FixedTick.
        /// </summary>
        void Separate(CarId id, Vector2 worldOffsetXZ);

        /// <summary>Apply a normalized damage delta (positive lowers health). Clamps to [0,1].</summary>
        void ApplyDamage(CarId id, float damageDelta);

        /// <summary>Set a stun lockout countdown (steer/throttle zeroed) for at least the given seconds.</summary>
        void SetStun(CarId id, float seconds);

        /// <summary>
        /// Nudge the car's heading by <paramref name="deltaRadians"/> without
        /// touching its velocity vector. Used by the car-car collision resolver
        /// to model "lose balance" — an off-centre bump rotates the chassis a
        /// few degrees, so the velocity vector momentarily diverges from the
        /// forward axis and the car visibly wobbles before the slip-grip term
        /// pulls it back. Idempotent if the car id is unknown.
        /// </summary>
        void PerturbHeading(CarId id, float deltaRadians);

        /// <summary>Read-only access to a car's static parameters (mass-equivalent, collision radius, etc).</summary>
        bool TryGetParameters(CarId id, out CarParameters parameters);
    }
}
