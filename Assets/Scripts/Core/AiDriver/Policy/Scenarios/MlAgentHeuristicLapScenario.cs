using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.AiDriver.Loop;
using UnityPpoRacingTrainer.Core.AiDriver.Physics;
using UnityPpoRacingTrainer.Core.Terrain;
using UnityPpoRacingTrainer.Core.Terrain.Scenarios;
using UnityPpoRacingTrainer.Core.Track;
using UnityPpoRacingTrainer.Core.Track.Loop;
using Unidad.Core.Grid;
using Unidad.Core.Testing;
using Unity.MLAgents.Policies;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.AiDriver.Policy.Scenarios
{
    /// <summary>
    /// Same stadium loop + terrain + camera as <see cref="HeuristicLapScenario"/>, but
    /// the car is driven by an <see cref="AiDriverAgentBehaviour"/> running ML-Agents
    /// in <see cref="BehaviorType.HeuristicOnly"/> mode. Proves the Agent shell, the
    /// policy service's heuristic path, and the observation pipeline work end-to-end
    /// without a Python trainer or ONNX model.
    /// </summary>
    internal sealed class MlAgentHeuristicLapScenario : DataDrivenScenario
    {
        private const int TerrainWidth = 20;
        private const int TerrainDepth = 20;
        private const int DecisionPeriodSteps = 5;

        // -- service graph (recreated per Execute) --
        private ScenarioEventBus _eventBus;
        private ScenarioGameObjectFactory _factory;
        private TerrainService _terrain;
        private TerrainMeshBuilder _terrainMeshBuilder;
        private TrackPieceCatalog _pieceCatalog;
        private TrackPieceMeshBuilder _pieceMeshBuilder;
        private List<ITrackPlacementValidator> _validators;
        private TrackPlacementService _placement;
        private ClosedLoopService _loopService;
        private TrackQueryService _trackQuery;
        private CarSimulationService _carSim;
        private DriverProfileRegistry _profileRegistry;
        private AiDriverPolicyService _policyService;

        // -- presentation --
        private GameObject _terrainObject;
        private Mesh _terrainMesh;
        private Material _terrainMaterial;
        private GameObject _agentObject;
        private GameObject _carVisual;
        private AiDriverAgentBehaviour _agent;
        private MlAgentHeuristicLapTickProxy _tickProxy;

        // -- runtime --
        private bool _agentReady;

        public MlAgentHeuristicLapScenario() : base(new TestScenarioDefinition(
            "ai-driver-mlagent-heuristic",
            "AI Driver — ML Agent (Heuristic mode)",
            "Same stadium loop as 'AI Driver — Heuristic Lap (Live)' but the car is " +
            "driven by an ML-Agents Agent in HeuristicOnly mode. Proves the agent shell + " +
            "policy service + observation pipeline work end-to-end without a trainer.",
            Array.Empty<ScenarioParameter>()))
        { }

        protected override void ExecuteInternal(ScenarioParameterOverrides overrides)
        {
            _eventBus = new ScenarioEventBus();
            _factory = new ScenarioGameObjectFactory();

            BuildTerrain();
            BuildTrackServices();
            BuildAiDriverServices();
            BuildLoopGeometry();
            SpawnAgent();
            HookTickProxy();
            ApplyIsoCamera();

            Debug.Log($"[MlAgentHeuristicLapScenario] Ready. Loop closed: {_loopService.IsLoopClosed}");
        }

        protected override ScenarioVerificationResult VerifyInternal(ScenarioParameterOverrides overrides)
        {
            var checks = new List<ScenarioVerificationResult.CheckResult>
            {
                new("loop closed",
                    _loopService != null && _loopService.IsLoopClosed,
                    "ClosedLoopService did not detect a loop after placement"),
                new("agent registered",
                    _agentReady && _agent != null && _agent.IsRegistered,
                    "AiDriverAgentBehaviour did not register a car with the policy service"),
                new("track query active",
                    _trackQuery != null && _trackQuery.HasLoop,
                    "TrackQueryService reports no active loop"),
            };
            return new ScenarioVerificationResult(checks);
        }

        protected override void OnCleanup()
        {
            if (_tickProxy != null) _tickProxy.OnFixedUpdate = null;
            _tickProxy = null;

            if (_agent != null && _agent.gameObject != null)
            {
                // Triggers OnDisable → policy.UnregisterAgent.
                UnityEngine.Object.DestroyImmediate(_agent.gameObject);
            }
            _agent = null;
            _agentReady = false;

            _carSim?.Dispose();
            _trackQuery?.Dispose();
            _loopService?.Dispose();
            _placement?.Dispose();
            _factory?.Dispose();
            _eventBus?.ClearAllSubscriptions();

            _carSim = null; _trackQuery = null; _loopService = null;
            _placement = null;
            _factory = null; _eventBus = null;

            DestroyAny(_terrainMesh); _terrainMesh = null;
            DestroyAny(_terrainMaterial); _terrainMaterial = null;

            _carVisual = null;
            _agentObject = null;
            _terrainObject = null;
        }

        // -------------------- build --------------------

        private void BuildTerrain()
        {
            _terrain = new TerrainService(_eventBus);
            _terrain.Initialize(new TerrainBuildOptions(TerrainWidth, TerrainDepth, 0, TrackPieceConstants.CellSize));

            _terrainMeshBuilder = new TerrainMeshBuilder();
            _terrainMesh = _terrainMeshBuilder.Build(_terrain, TerrainPalette.BoneAndSlate, TerrainColorMode.Palette);
            _terrainMaterial = ResolveOpaqueMaterial("AiDriver_MlAgentLap_TerrainMat");

            _terrainObject = _factory.CreateEmpty("[MlAgentLap] Terrain");
            _terrainObject.transform.SetParent(SceneRoot.transform, false);

            var mf = _terrainObject.AddComponent<MeshFilter>();
            mf.sharedMesh = _terrainMesh;
            var mr = _terrainObject.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _terrainMaterial;
        }

        private void BuildTrackServices()
        {
            _pieceCatalog = new TrackPieceCatalog();
            TrackPieceCatalogSeeder.Seed(_pieceCatalog);

            _pieceMeshBuilder = new TrackPieceMeshBuilder(new FlatHeightAdapter());
            _validators = new List<ITrackPlacementValidator>
            {
                new BoundsValidator(),
                new OverlapValidator(),
                new TerrainCompatibilityValidator()
            };

            _placement = new TrackPlacementService(_eventBus, _pieceCatalog, _pieceMeshBuilder,
                _validators, _terrain, _factory, TrackPalette.Default);
        }

        private void BuildAiDriverServices()
        {
            _loopService = new ClosedLoopService(_eventBus, _placement, _pieceCatalog, _terrain);
            _trackQuery = new TrackQueryService(_eventBus, _loopService);
            _carSim = new CarSimulationService(_eventBus, _trackQuery);
            _profileRegistry = new DriverProfileRegistry();
            // Scenario constructs the policy directly (no DI container). Latest
            // profile with a null reward shaper is the right fit for a diagnostic
            // heuristic-lap scene; obs/physics route through the canonical path.
            var versionProfile = new Versions.Latest.LatestVersionProfile(() => new Versions.Latest.NullRewardShaper());
            _policyService = new AiDriverPolicyService(_eventBus, _carSim, _trackQuery, _loopService, _profileRegistry, versionProfile);
            // Scenario uses fixed lap-start spawn (longest-straight midpoint).
            _policyService.Spawn = SpawnStrategy.LongestStraightMidpoint;
        }

        private void BuildLoopGeometry()
        {
            // Same stadium as HeuristicLapScenario.
            const int xMin = 3;
            const int xMax = 16;
            const int zMin = 8;
            const int zMax = 12;

            for (int x = xMin + 1; x < xMax; x++)
                TryPlace(TrackPieceShapes.Straight_1x1, x, zMin, TrackDirection.East);
            TryPlace(TrackPieceShapes.Curve_1x1, xMax, zMin, TrackDirection.South);
            for (int z = zMin + 1; z < zMax; z++)
                TryPlace(TrackPieceShapes.Straight_1x1, xMax, z, TrackDirection.North);
            TryPlace(TrackPieceShapes.Curve_1x1, xMax, zMax, TrackDirection.East);
            for (int x = xMin + 1; x < xMax; x++)
                TryPlace(TrackPieceShapes.Straight_1x1, x, zMax, TrackDirection.East);
            TryPlace(TrackPieceShapes.Curve_1x1, xMin, zMax, TrackDirection.North);
            for (int z = zMin + 1; z < zMax; z++)
                TryPlace(TrackPieceShapes.Straight_1x1, xMin, z, TrackDirection.North);
            TryPlace(TrackPieceShapes.Curve_1x1, xMin, zMin, TrackDirection.West);
        }

        private void SpawnAgent()
        {
            if (!_loopService.TryGetCurrentLoop(out var loop)) return;

            // Single source of truth — see ClosedLoop.GetCanonicalStartPose.
            var pose = loop.GetCanonicalStartPose();
            Vector3 spawnPos = pose.position;
            float heading = loop.GetCanonicalStartHeading();

            // Same start-line + sector visualization the trainer uses.
            SectorBoundaryDebugRenderer.MountOn(SceneRoot, _loopService);

            _agentObject = _factory.CreateEmpty("[MlAgentLap] Agent");
            _agentObject.transform.SetParent(SceneRoot.transform, false);

            // Order matters: BehaviorParameters first (Agent [RequireComponent]s it),
            // then AiDriverAgentBehaviour (an Agent subclass — DecisionRequester's
            // [RequireComponent(typeof(Agent))] is satisfied by ours), then
            // DecisionRequester. Adding DecisionRequester first makes Unity auto-add a
            // BASE Agent instance and ML-Agents picks that empty agent over our subclass.
            var behaviorParams = _agentObject.AddComponent<BehaviorParameters>();
            behaviorParams.BehaviorName = "RacingDriver";
            behaviorParams.BehaviorType = BehaviorType.HeuristicOnly;
            behaviorParams.BrainParameters.VectorObservationSize = RacingObservationLayout.FloatsPerFrame;
            behaviorParams.BrainParameters.NumStackedVectorObservations = 3;
            behaviorParams.BrainParameters.ActionSpec = Unity.MLAgents.Actuators.ActionSpec.MakeContinuous(2);

            _agent = _agentObject.AddComponent<AiDriverAgentBehaviour>();
            _agent.Configure("default", spawnPos, heading);
            _agent.BindPolicy(_policyService);
            _agentReady = _agent.IsRegistered;

            var requester = _agentObject.AddComponent<Unity.MLAgents.DecisionRequester>();
            requester.DecisionPeriod = DecisionPeriodSteps;
            requester.TakeActionsBetweenDecisions = true;

            // Visual cube parented to the agent so it follows the simulated car.
            _carVisual = _factory.CreatePrimitive(PrimitiveType.Cube, "[MlAgentLap] CarVisual",
                spawnPos + new Vector3(0f, 0.1f, 0f));
            _carVisual.transform.SetParent(SceneRoot.transform, false);
            _carVisual.transform.localScale = new Vector3(0.25f, 0.18f, 0.4f);
            var renderer = _carVisual.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                var mat = ResolveOpaqueMaterial("AiDriver_MlAgentLap_CarMat");
                mat.color = new Color(0.20f, 0.85f, 0.85f, 1f); // teal accent (vs heuristic-scenario red)
                renderer.sharedMaterial = mat;
            }
        }

        private void HookTickProxy()
        {
            var proxyGo = _factory.CreateEmpty("[MlAgentLap] TickProxy");
            proxyGo.transform.SetParent(SceneRoot.transform, false);
            _tickProxy = proxyGo.AddComponent<MlAgentHeuristicLapTickProxy>();
            _tickProxy.OnFixedUpdate = OnFixedUpdate;
            _tickProxy.OnUpdate = OnUpdate;
        }

        private void ApplyIsoCamera()
        {
            var cam = Camera.main;
            if (cam == null) return;
            float c = TrackPieceConstants.CellSize;
            var target = new Vector3(TerrainWidth * 0.5f * c, 0f, TerrainDepth * 0.5f * c);
            cam.transform.position = target + new Vector3(10f * c, 14f * c, -10f * c);
            cam.transform.LookAt(target);
        }

        private void OnFixedUpdate(float dt)
        {
            _carSim?.FixedTick(dt);
        }

        private void OnUpdate(float dt)
        {
            if (!_agentReady) return;
            if (!_carSim.TryGetState(_agent.CarId, out var state)) return;
            if (_carVisual == null) return;
            _carVisual.transform.position = state.Position + new Vector3(0f, 0.1f, 0f);
            _carVisual.transform.rotation = Quaternion.Euler(0f, state.Heading * Mathf.Rad2Deg, 0f);
        }

        // -------------------- helpers --------------------

        private void TryPlace(TrackPieceShape shape, int x, int z, TrackDirection facing)
        {
            var r = _placement.TryPlace(shape, new GridPosition(x, z), facing);
            if (!r.Success)
                Debug.LogError($"[MlAgentHeuristicLapScenario] placement failed: {shape.Id} @ ({x},{z}) face={facing}: {r.Reason}");
        }

        private Material ResolveOpaqueMaterial(string name)
        {
            var shader = Shader.Find("RaceConstructor/TerrainVertexColor")
                         ?? Shader.Find("Universal Render Pipeline/Lit")
                         ?? Shader.Find("Standard");
            return new Material(shader) { name = name };
        }

        private static void DestroyAny(UnityEngine.Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(o);
            else UnityEngine.Object.DestroyImmediate(o);
        }
    }
}
