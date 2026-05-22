using UnityPpoRacingTrainer.Core.Ghost.Presentation;
using UnityPpoRacingTrainer.Core.Ghost.Simulation;
using UnityPpoRacingTrainer.Core.Track.Topology;
using Unidad.Core.Abstractions;
using Unidad.Core.EventBus;
using Unidad.Core.Systems;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Ghost.Director
{
    internal sealed class GameSceneDirector : SystemServiceBase, IGameSceneDirector, ITickable
    {
        // Brief delay before re-dropping so the player perceives the reset as a
        // discrete event rather than an instant teleport-and-fall.
        private const float RespawnDelaySeconds = 0.15f;

        private readonly ITrackEndingService _topology;
        private readonly IGhostDriverService _driver;
        private readonly IGhostCarPresenter _presenter;
        private readonly IGhostSpawnAnimator _animator;

        private GhostDirectorState _state = GhostDirectorState.Idle;
        private IGhostSpawnHandle _activeHandle;
        private bool _topologyDirty;
        private float _respawnTimer;
        private GhostSpawnCause _pendingCause = GhostSpawnCause.InitialSpawn;

        public GameSceneDirector(
            IEventBus eventBus,
            ITrackEndingService topology,
            IGhostDriverService driver,
            IGhostCarPresenter presenter,
            IGhostSpawnAnimator animator) : base(eventBus)
        {
            _topology = topology;
            _driver = driver;
            _presenter = presenter;
            _animator = animator;

            Subscribe<TrackTopologyChangedEvent>(_ => _topologyDirty = true);
            Subscribe<GhostLapCompletedEvent>(_ =>
            {
                if (_state == GhostDirectorState.Drive)
                    BeginRespawn(GhostSpawnCause.LapCompleted);
            });
            Subscribe<GhostOffTrackEvent>(_ =>
            {
                if (_state == GhostDirectorState.Drive)
                    BeginRespawn(GhostSpawnCause.OffTrack);
            });
        }

        public GhostDirectorState CurrentState => _state;

        public void StartGhostLoop()
        {
            if (_state != GhostDirectorState.Idle) return;
            BeginSpawnDrop(GhostSpawnCause.InitialSpawn);
        }

        public void Tick(float deltaTime)
        {
            // Topology mutated mid-flight → defer respawn to next natural exit
            // out of SpawnDrop / Settle, OR trigger immediately if mid-Drive.
            if (_topologyDirty && _state == GhostDirectorState.Drive)
            {
                _topologyDirty = false;
                BeginRespawn(GhostSpawnCause.TrackChanged);
                return;
            }

            switch (_state)
            {
                case GhostDirectorState.SpawnDrop: TickSpawnDrop(deltaTime); break;
                case GhostDirectorState.Settle: TickSettle(deltaTime); break;
                case GhostDirectorState.Drive: TickDrive(); break;
                case GhostDirectorState.Respawn: TickRespawn(deltaTime); break;
            }
        }

        private void BeginSpawnDrop(GhostSpawnCause cause)
        {
            if (!_topology.TryGetStartLine(out var landPos, out var heading))
            {
                // Nothing placed yet → wait. Will retry on the next topology change.
                return;
            }

            _pendingCause = cause;
            if (!_driver.HasSpawned)
                _driver.Spawn(landPos, heading);
            else
                _driver.Teleport(landPos, heading);

            _driver.DriveEnabled = false;
            _activeHandle = _animator.PlayDropFromAir(landPos, heading);
            _state = GhostDirectorState.SpawnDrop;
            Publish(new GhostSpawnRequestedEvent(landPos, heading, cause));
        }

        private void TickSpawnDrop(float dt)
        {
            _activeHandle.Update(dt);
            if (_driver.TryReadSnapshot(out var snap))
            {
                _presenter.Show(
                    snap.Position + new Vector3(0f, _activeHandle.CurrentYOffset, 0f),
                    snap.Heading,
                    bodyLeanRad: 0f,
                    alpha: _activeHandle.CurrentAlpha);
            }

            if (_activeHandle.IsLanded)
            {
                Publish(new GhostLandedEvent(_driver.GhostId));
                _state = GhostDirectorState.Settle;
            }
        }

        private void TickSettle(float dt)
        {
            _activeHandle.Update(dt);
            if (_driver.TryReadSnapshot(out var snap))
            {
                _presenter.Show(
                    snap.Position + new Vector3(0f, _activeHandle.CurrentYOffset, 0f),
                    snap.Heading,
                    bodyLeanRad: 0f,
                    alpha: _activeHandle.CurrentAlpha);
            }

            if (_activeHandle.IsSettleComplete)
            {
                Publish(new GhostSettledEvent(_driver.GhostId));
                _driver.DriveEnabled = true;
                _state = GhostDirectorState.Drive;
                Publish(new GhostDrivingStartedEvent(_driver.GhostId));

                // If topology shifted mid-flight, queue a respawn on the next tick.
                if (_topologyDirty)
                {
                    _topologyDirty = false;
                    BeginRespawn(GhostSpawnCause.TrackChanged);
                }
            }
        }

        private void TickDrive()
        {
            if (_driver.TryReadSnapshot(out var snap))
            {
                _presenter.Show(snap.Position, snap.Heading, bodyLeanRad: 0f, alpha: 1f);
            }
        }

        private void BeginRespawn(GhostSpawnCause cause)
        {
            _pendingCause = cause;
            _driver.DriveEnabled = false;
            _presenter.Hide();
            _respawnTimer = 0f;
            _state = GhostDirectorState.Respawn;
        }

        private void TickRespawn(float dt)
        {
            _respawnTimer += dt;
            if (_respawnTimer < RespawnDelaySeconds) return;

            Publish(new GhostRespawnedEvent(_driver.GhostId, _pendingCause));
            BeginSpawnDrop(_pendingCause);
        }
    }
}
