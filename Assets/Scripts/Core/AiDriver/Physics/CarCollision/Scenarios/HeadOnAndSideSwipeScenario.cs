using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.AiDriver.Policy;
using UnityPpoRacingTrainer.Core.Terrain.Scenarios;
using Unidad.Core.Testing;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.AiDriver.Physics.CarCollision.Scenarios
{
    /// <summary>
    /// Drives the car-car collision resolver against a scripted stub sim with
    /// two cars: head-on (closing fast), then a side-swipe. Logs the produced
    /// CarHitCarEvents and the impulses sent to the stub.
    /// </summary>
    internal sealed class HeadOnAndSideSwipeScenario : DataDrivenScenario
    {
        private ScenarioEventBus _eventBus;
        private StubSim _sim;
        private CarCollisionService _collisionService;
        private int _hitCount;

        public HeadOnAndSideSwipeScenario() : base(new TestScenarioDefinition(
            "ai-driver-carcar-collision",
            "AI Driver — Car-Car Collision (Synthetic)",
            "Scripted overlap between two cars with a stub sim. Counts CarHitCarEvents.",
            Array.Empty<ScenarioParameter>()))
        { }

        protected override void ExecuteInternal(ScenarioParameterOverrides overrides)
        {
            _eventBus = new ScenarioEventBus();
            _sim = new StubSim();
            _collisionService = new CarCollisionService(_eventBus, _sim);
            _collisionService.RegisterDriver(new CarId(1), DriverPersonality.Attacker);
            _collisionService.RegisterDriver(new CarId(2), DriverPersonality.Defender);

            _eventBus.Subscribe<CarHitCarEvent>(evt =>
            {
                _hitCount++;
                Debug.Log($"[HeadOnAndSideSwipeScenario] HIT a={evt.A} b={evt.B} impact={evt.ImpactSpeed:F2}");
            });

            // Head-on: car 1 at +x, car 2 at +x+0.5, closing fast.
            _sim.Place(new CarId(1), new Vector3(0f, 0f, 0f), new Vector2(20f, 0f));
            _sim.Place(new CarId(2), new Vector3(0.5f, 0f, 0f), new Vector2(-20f, 0f));
            _collisionService.FixedTick(1f / 50f);

            // Side-swipe: same row, slight lateral, modest closing.
            _sim.Place(new CarId(1), new Vector3(2f, 0f, 0f), new Vector2(15f, 1f));
            _sim.Place(new CarId(2), new Vector3(2.6f, 0f, 0.5f), new Vector2(15f, -1f));
            _collisionService.FixedTick(1f / 50f);

            Debug.Log($"[HeadOnAndSideSwipeScenario] total hits={_hitCount} impulses sent={_sim.Impulses}");
        }

        protected override ScenarioVerificationResult VerifyInternal(ScenarioParameterOverrides overrides)
            => new(new List<ScenarioVerificationResult.CheckResult>
            {
                new("collision service constructed", _collisionService != null, "CarCollisionService not built"),
            });

        protected override void OnCleanup()
        {
            _collisionService?.Dispose();
            _collisionService = null;
            _sim = null;
            _eventBus = null;
        }

        private sealed class StubSim : ICarSimulationService
        {
            private readonly Dictionary<CarId, CarState> _states = new();
            private readonly Dictionary<CarId, CarParameters> _params = new();
            public int Impulses { get; private set; }

            public IReadOnlyCollection<CarId> ActiveCars => _states.Keys;

            public void Place(CarId id, Vector3 pos, Vector2 velXZ)
            {
                _states[id] = new CarState { Position = pos, VelocityXZ = velXZ, OnGround = true };
                _params[id] = AiDriverPhysicsDefaults.Latest;
            }

            public CarId Spawn(Vector3 position, float heading, CarParameters parameters) => default;
            public void RespawnExisting(CarId id, Vector3 position, float heading, CarParameters parameters) { _states[id] = new CarState { Position = position, Heading = heading, OnGround = true }; _params[id] = parameters; }
            public void Despawn(CarId id) { _states.Remove(id); _params.Remove(id); }
            public void SetInput(CarId id, DriverInput input) { }
            public bool TryGetState(CarId id, out CarState state) => _states.TryGetValue(id, out state);
            public void TeleportTo(CarId id, Vector3 position, float heading) { }
            public bool TryGetHealth(CarId id, out float health) { health = 1f; return true; }
            public void ApplyImpulse(CarId id, Vector2 deltaVelocityXZ) { Impulses++; if (_states.TryGetValue(id, out var s)) { s.VelocityXZ += deltaVelocityXZ; _states[id] = s; } }
            public void Separate(CarId id, Vector2 worldOffsetXZ) { if (_states.TryGetValue(id, out var s)) { s.Position = new Vector3(s.Position.x + worldOffsetXZ.x, s.Position.y, s.Position.z + worldOffsetXZ.y); _states[id] = s; } }
            public void ApplyDamage(CarId id, float damageDelta) { }
            public void SetStun(CarId id, float seconds) { }
            public void PerturbHeading(CarId id, float deltaRadians) { if (_states.TryGetValue(id, out var s)) { s.Heading += deltaRadians; _states[id] = s; } }
            public bool TryGetParameters(CarId id, out CarParameters parameters) => _params.TryGetValue(id, out parameters);
        }
    }
}
