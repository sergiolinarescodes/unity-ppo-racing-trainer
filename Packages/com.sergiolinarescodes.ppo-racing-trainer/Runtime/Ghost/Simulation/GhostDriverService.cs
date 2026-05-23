using UnityPpoRacingTrainer.Core.AiDriver;
using UnityPpoRacingTrainer.Core.AiDriver.Internal;
using UnityPpoRacingTrainer.Core.AiDriver.Loop;
using UnityPpoRacingTrainer.Core.AiDriver.Physics;
using UnityPpoRacingTrainer.Core.AiDriver.Policy;
using UnityPpoRacingTrainer.Core.AiDriver.Versions;
using UnityPpoRacingTrainer.Core.Track;
using Unidad.Core.Abstractions;
using Unidad.Core.EventBus;
using Unidad.Core.Grid;
using Unidad.Core.Systems;
using Unity.InferenceEngine;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Ghost.Simulation
{
    /// <summary>
    /// Owns the singleton ghost car for the main game scene. The driving itself
    /// is delegated to the canonical ML-Agents shell (<see cref="AiDriverAgentBehaviour"/>)
    /// using the bootstrap-selected <see cref="IAiDriverVersionProfile"/> — this
    /// service is responsible for instantiating the prefab, patching its
    /// <see cref="BehaviorParameters"/> from the profile, and keeping the car
    /// in sync with the placed-pieces topology (teleport to start + EndEpisode
    /// on every new piece so the model immediately re-engages with the updated
    /// strip).
    /// </summary>
    internal sealed class GhostDriverService : SystemServiceBase, IGhostDriverService, ITickable
    {
        // Cap on a single drive segment so a stalled ghost still respawns.
        private const float MaxDriveSegmentSec = 30f;
        // Off-track grace before publishing GhostOffTrackEvent. Trainer's
        // EpisodeRunner uses 0.5s; the game is more lenient (2s) so an
        // aggressive apex / brief lateral excursion has time to recover
        // without yanking the ghost back to spawn mid-corner.
        private const float OpenStripOffTrackGraceSec = 2f;

        private readonly ICarSimulationService _carSim;
        private readonly ITrackQueryService _trackQuery;
        private readonly ITrackPlacementService _placement;
        private readonly Terrain.ITerrainService _terrain;
        private readonly IAiDriverVersionProfile _profile;
        private readonly IAiDriverPolicyService _policy;
        // Shared with RewardShaper: single entry-point that attaches a CarId to
        // tire/fuel/collision and publishes DriverPersonalityChangedEvent.
        // Optional so legacy scenarios without side-systems still work.
        private readonly IDriverPhysicsRegistry _physicsRegistry;

        private CarId _ghostId;
        private bool _hasSpawned;
        private bool _driveEnabled;
        private int _lastLapsCompleted;
        private float _segmentTime;
        private float _offTrackTimer;
        private float _lapStartTime;

        private GameObject _agentGo;
        private AiDriverAgentBehaviour _agentBehaviour;
        private DecisionRequester _decisionRequester;
        private Vector3 _lastSpawnPos;
        private float _lastSpawnHeading;
        private bool _pendingReset;

        public GhostDriverService(
            IEventBus eventBus,
            ICarSimulationService carSim,
            ITrackQueryService trackQuery,
            ITrackPlacementService placement,
            IAiDriverVersionProfile profile,
            IAiDriverPolicyService policy,
            Terrain.ITerrainService terrain = null,
            IDriverPhysicsRegistry physicsRegistry = null) : base(eventBus)
        {
            _carSim = carSim;
            _trackQuery = trackQuery;
            _placement = placement;
            _terrain = terrain;
            _profile = profile;
            _policy = policy;
            _physicsRegistry = physicsRegistry;

            Subscribe<TrackPiecePlacedEvent>(_ => _pendingReset = true);
        }

        public CarId GhostId => _ghostId;
        public bool HasSpawned => _hasSpawned;

        public bool DriveEnabled
        {
            get => _driveEnabled;
            set
            {
                _driveEnabled = value;
                if (value) _segmentTime = 0f;
                if (_hasSpawned && !value)
                    _carSim.SetInput(_ghostId, new DriverInput(0f, 0f, false));
            }
        }

        public void Spawn(Vector3 worldPos, float heading)
        {
            if (_hasSpawned) return;

            var prefab = PackageResourceLoader.Load<GameObject>(_profile.PrefabResourcePath, ".prefab");
            if (prefab == null)
            {
                Debug.LogError($"[GhostDriver] AiDriverAgent prefab missing at Resources/{_profile.PrefabResourcePath}.prefab (version={_profile.VersionId}).");
                return;
            }

            // Deactivate prefab BEFORE Instantiate so Agent.Awake/OnEnable do
            // not run before we've patched BehaviorParameters. Mirrors
            // TrainerBootstrap.SpawnAgent.
            bool wasActive = prefab.activeSelf;
            if (wasActive) prefab.SetActive(false);
            _agentGo = Object.Instantiate(prefab);
            if (wasActive) prefab.SetActive(true);

            _agentBehaviour = _agentGo.GetComponent<AiDriverAgentBehaviour>();
            if (_agentBehaviour == null)
            {
                Debug.LogError($"[GhostDriver] Prefab Resources/{_profile.PrefabResourcePath} has no AiDriverAgentBehaviour. Aborting ghost spawn.");
                Object.Destroy(_agentGo);
                _agentGo = null;
                return;
            }

            var brain = _agentGo.GetComponent<BehaviorParameters>();
            if (brain != null)
            {
                brain.BrainParameters.VectorObservationSize = _profile.FloatsPerFrame;
                brain.BrainParameters.NumStackedVectorObservations = 3;
                brain.BrainParameters.ActionSpec = ActionSpec.MakeContinuous(2);
                brain.BehaviorName = _profile.BehaviorName;

                string resolvedPath = ResolveOnnxResourcePath(_profile.OnnxResourcePath);
                if (!string.IsNullOrEmpty(resolvedPath))
                {
                    var model = PackageResourceLoader.Load<ModelAsset>(resolvedPath, ".onnx");
                    if (model != null)
                    {
                        brain.Model = model;
                        Debug.Log($"[GhostDriver] Loaded model {model.name} (version={_profile.VersionId}).");
                    }
                    else
                    {
                        Debug.LogError($"[GhostDriver] ONNX missing at Resources/{resolvedPath}.onnx — falling back to prefab m_Model.");
                    }
                }

                // Main scene has no Python trainer attached — force inference.
                // Without this, BehaviorType.Default falls back to Heuristic()
                // (which is pure-pursuit on the policy service, NOT what we want).
                brain.BehaviorType = BehaviorType.InferenceOnly;

                // Sample from the Gaussian (default) instead of using the
                // deterministic mean. The PPO policy was trained with action
                // noise present in every trajectory, so its "correct" braking
                // distance learns to compensate for that noise — feeding it
                // the mean alone makes it brake too late on tight corners.
                // Matches RealisticLoopInferenceScenario (the canonical
                // trainer-side eval scene) which also leaves this false.
                brain.DeterministicInference = false;
            }
            else
            {
                Debug.LogError($"[GhostDriver] Prefab Resources/{_profile.PrefabResourcePath} has no BehaviorParameters.");
            }

            _decisionRequester = _agentGo.GetComponent<DecisionRequester>();
            _agentBehaviour.Configure("default", worldPos, heading, 0);
            // Mirror TrainerBootstrap.SpawnAgent — same component the trainer
            // attaches before SetActive so any visualisation hook + event
            // subscriptions on the visualizer fire in the same order in both
            // contexts.
            _agentGo.AddComponent<AiDriverAgentVisualizer>();
            _agentGo.SetActive(true);

            // If Initialize() didn't run (e.g. scene container missing), the
            // policy never registered the agent → no CarSim car → no driving.
            // Drive the resolution explicitly using the canonical scene
            // container (the agent's own gameObject.scene path didn't always
            // resolve cleanly when instantiated post-bootstrap).
            if (!_agentBehaviour.IsRegistered)
            {
                Debug.LogWarning("[GhostDriver] Agent did not self-register on Initialize — falling back to manual BindPolicy.");
                _agentBehaviour.BindPolicy(_policy);
            }

            _ghostId = _agentBehaviour.CarId;
            // Attach the ghost to the same per-driver physics modifier stack
            // (tires, fuel, car-car collision) the trainer wires up at episode
            // begin. Without this the model — trained with all three modifiers
            // active — drives against bare base physics and behaves differently
            // (less grip falloff, no fuel-mass effect, default collision
            // response). The registry no-ops on services that weren't installed.
            _physicsRegistry?.Register(_ghostId, GhostDriverDefaults.Personality, GhostDriverDefaults.StartingFuelLiters);
            // Mirror EpisodeRunner's per-episode init path. In trainer, every
            // episode begin calls policy.BeginEpisode(carId) which re-seats
            // Smoother, clears PrevHeading, and walks the reward-shaper episode
            // hook. Game has no EpisodeRunner, so we trigger the same init
            // explicitly so the first decision sees the identical agent
            // record state the trainer's first decision sees.
            _policy.BeginEpisode(_ghostId);
            Debug.Log($"[GhostDriver] Agent registered: carId={_ghostId.Value}, isRegistered={_agentBehaviour.IsRegistered}, decisionRequester.enabled={_decisionRequester?.enabled}");
            _hasSpawned = true;
            _lastSpawnPos = worldPos;
            _lastSpawnHeading = heading;
            _lastLapsCompleted = 0;
            _segmentTime = 0f;
            _offTrackTimer = 0f;
            _lapStartTime = 0f;
            _pendingReset = false;
        }

        public void Teleport(Vector3 worldPos, float heading)
        {
            if (!_hasSpawned) return;
            _carSim.TeleportTo(_ghostId, worldPos, heading);
            _lastSpawnPos = worldPos;
            _lastSpawnHeading = heading;
            _lastLapsCompleted = 0;
            _segmentTime = 0f;
            _offTrackTimer = 0f;
            _lapStartTime = 0f;
            _pendingReset = false;
            // Fresh segment → fresh tire wear + full tank. Mirrors the
            // trainer's _tires.Reset / fuel re-register on episode begin.
            _physicsRegistry?.Reset(_ghostId, GhostDriverDefaults.StartingFuelLiters);
            // EndEpisode flushes LSTM state so the model re-evaluates from the
            // new spawn pose — pairs with Teleport's intent ("start over here").
            if (_agentBehaviour != null) _agentBehaviour.EndEpisode();
        }

        public void Despawn()
        {
            if (!_hasSpawned) return;
            // Agent.OnDisable calls policy.UnregisterAgent — destroying the GO
            // is enough; no manual UnregisterAgent / Despawn on _carSim.
            if (_agentGo != null) Object.Destroy(_agentGo);
            _agentGo = null;
            _agentBehaviour = null;
            _decisionRequester = null;
            _hasSpawned = false;
            _driveEnabled = false;
            _pendingReset = false;
        }

        public bool TryReadSnapshot(out GhostSimSnapshot snap)
        {
            if (!_hasSpawned || !_carSim.TryGetState(_ghostId, out var state))
            {
                snap = default; return false;
            }
            bool off = false;
            if (_trackQuery.HasLoop)
            {
                var proj = _trackQuery.Project(state.Position, state.LastAnchorIndex);
                off = proj.IsOffTrack;
            }
            else
            {
                off = !IsInsidePlacedFootprint(state.Position);
            }
            float lean = 0f;
            snap = new GhostSimSnapshot(state.Position, state.Heading, state.Speed, off,
                state.LapsCompleted, lean);
            return true;
        }

        public void Tick(float deltaTime)
        {
            if (!_hasSpawned) return;

            // Drain piece-placed reset — debounced to once-per-frame so a
            // burst of placements (paste / undo replay) only resets once.
            if (_pendingReset)
            {
                _pendingReset = false;
                _carSim.TeleportTo(_ghostId, _lastSpawnPos, _lastSpawnHeading);
                _lastLapsCompleted = 0;
                _segmentTime = 0f;
                _offTrackTimer = 0f;
                _lapStartTime = 0f;
                _physicsRegistry?.Reset(_ghostId, GhostDriverDefaults.StartingFuelLiters);
                if (_agentBehaviour != null) _agentBehaviour.EndEpisode();
                return;
            }

            if (!_carSim.TryGetState(_ghostId, out var state)) return;
            if (!_driveEnabled) return;

            _segmentTime += deltaTime;

            if (_trackQuery.HasLoop)
            {
                int laps = state.LapsCompleted;
                if (laps > _lastLapsCompleted)
                {
                    float lapTime = _segmentTime - _lapStartTime;
                    _lapStartTime = _segmentTime;
                    _lastLapsCompleted = laps;
                    Publish(new GhostLapCompletedEvent(_ghostId, laps, lapTime));
                }

                var proj = _trackQuery.Project(state.Position, state.LastAnchorIndex);
                if (proj.IsOffTrack)
                {
                    _offTrackTimer += deltaTime;
                    if (_offTrackTimer >= OpenStripOffTrackGraceSec)
                    {
                        Publish(new GhostOffTrackEvent(_ghostId, state.Position));
                        _offTrackTimer = 0f;
                    }
                }
                else
                {
                    _offTrackTimer = 0f;
                }
            }
            else
            {
                if (!IsInsidePlacedFootprint(state.Position))
                {
                    _offTrackTimer += deltaTime;
                    if (_offTrackTimer >= OpenStripOffTrackGraceSec)
                    {
                        Publish(new GhostOffTrackEvent(_ghostId, state.Position));
                        _offTrackTimer = 0f;
                    }
                }
                else
                {
                    _offTrackTimer = 0f;
                }
            }

            if (_segmentTime > MaxDriveSegmentSec)
            {
                Publish(new GhostOffTrackEvent(_ghostId, state.Position));
                _segmentTime = 0f;
            }
        }

        private bool IsInsidePlacedFootprint(Vector3 worldPos)
        {
            if (_placement.Occupancy == null || _placement.Occupancy.Count == 0) return false;
            float cellSize = _terrain != null && _terrain.IsInitialized ? _terrain.CellSize : 1f;
            int cx = Mathf.FloorToInt(worldPos.x / cellSize);
            int cz = Mathf.FloorToInt(worldPos.z / cellSize);
            return _placement.Occupancy.ContainsKey(new GridPosition(cx, cz));
        }

        // "latest" sentinel → highest-version RacingDriver-v<MAJOR>(.<MINOR>)?-*.onnx
        // under Resources/AiDriver/Policies (Editor only); mtime is the tiebreak so a
        // freshly trained variant of the same version wins over older checkpoints.
        // Built players fall back to prefab m_Model.
        // Mirror of TrainerBootstrap.ResolveOnnxResourcePath — keep both in sync.
        private static string ResolveOnnxResourcePath(string profilePath)
        {
            if (!string.Equals(profilePath, "latest", System.StringComparison.OrdinalIgnoreCase))
                return profilePath;
#if UNITY_EDITOR
            var dir = new System.IO.DirectoryInfo("Assets/Resources/AiDriver/Policies");
            if (!dir.Exists) return null;
            System.IO.FileInfo best = null;
            (int major, int minor) bestVer = (-1, -1);
            foreach (var f in dir.GetFiles("RacingDriver-*.onnx"))
            {
                var ver = ParseVersionSuffix(f.Name);
                if (best == null
                    || ver.major > bestVer.major
                    || (ver.major == bestVer.major && ver.minor > bestVer.minor)
                    || (ver.major == bestVer.major && ver.minor == bestVer.minor
                        && f.LastWriteTimeUtc > best.LastWriteTimeUtc))
                {
                    best = f;
                    bestVer = ver;
                }
            }
            if (best == null) return null;
            return "AiDriver/Policies/" + System.IO.Path.GetFileNameWithoutExtension(best.Name);
#else
            return null;
#endif
        }

        // RacingDriver-v3.4-cold-6599k.onnx → (3, 4)
        // RacingDriver-v2-fix-foo-112599646.onnx → (2, 0)
        // Unparseable → (-1, -1) so well-named files always win.
        private static (int major, int minor) ParseVersionSuffix(string fileName)
        {
            const string prefix = "RacingDriver-v";
            if (!fileName.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase)) return (-1, -1);
            int i = prefix.Length;
            int majorStart = i;
            while (i < fileName.Length && char.IsDigit(fileName[i])) i++;
            if (i == majorStart) return (-1, -1);
            int major = int.Parse(fileName.Substring(majorStart, i - majorStart));
            int minor = 0;
            if (i < fileName.Length && fileName[i] == '.')
            {
                int minorStart = ++i;
                while (i < fileName.Length && char.IsDigit(fileName[i])) i++;
                if (i > minorStart) minor = int.Parse(fileName.Substring(minorStart, i - minorStart));
            }
            return (major, minor);
        }
    }
}
