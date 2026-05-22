using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.AiDriver.Loop;
using UnityPpoRacingTrainer.Core.AiDriver.Policy;
using UnityPpoRacingTrainer.Core.Terrain;
using UnityPpoRacingTrainer.Core.Terrain.Scenarios;
using UnityPpoRacingTrainer.Core.Track;
using UnityPpoRacingTrainer.Core.Track.Loop;
using UnityPpoRacingTrainer.Core.Track.Ribbon;
using Unidad.Core.Grid;
using Unidad.Core.Testing;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityPpoRacingTrainer.Core.AiDriver.Physics.Scenarios
{
    /// <summary>
    /// Visual lap scenario without ML. Builds a flat 20×20 terrain with a stadium
    /// loop (long sides 12 straights, short sides 3 straights, four 90° corners),
    /// spawns one car, and drives it with an inline pure-pursuit heuristic. Proves
    /// closed-loop → track query → kinematic car physics end-to-end and gives the
    /// AI a real straightaway to read lookahead curvature on (vs. the tight 4-curve
    /// loop used by unit tests).
    /// </summary>
    internal sealed class HeuristicLapScenario : DataDrivenScenario
    {
        private const int TerrainWidth = 20;
        private const int TerrainDepth = 20;
        // ~1.2 cells ahead — pure-pursuit horizon scales with cell size so the heuristic
        // reads "one tile ahead" regardless of grid scale.
        private const float LookaheadMeters = 1.2f * TrackPieceConstants.CellSize;
        private const float SteerGain = 2.0f;
        private const float MaxThrottle = 1.0f;

        private static readonly Color CarColor = new(0.95f, 0.25f, 0.25f, 1f);

        // -- service graph (recreated per Execute) --
        private ScenarioEventBus _eventBus;
        private ScenarioGameObjectFactory _factory;
        private TerrainService _terrain;
        private TerrainMeshBuilder _terrainMeshBuilder;
        private TrackPieceCatalog _pieceCatalog;
        private TrackPieceMeshBuilder _pieceMeshBuilder;
        private List<ITrackPlacementValidator> _validators;
        private TrackPlacementService _placement;
        private TrackRibbonService _ribbonService;
        private ClosedLoopService _loopService;
        private TrackQueryService _trackQuery;
        private CarSimulationService _carSim;

        // -- presentation --
        private GameObject _terrainObject;
        private Mesh _terrainMesh;
        private Material _terrainMaterial;
        private GameObject _carVisual;
        private HeuristicLapTickProxy _tickProxy;
        private Label _hudSpeed;
        private Label _hudLap;
        private Label _hudLateral;
        private Label _hudHeadingError;
        private Label _hudBoost;

        // -- runtime --
        private CarId _carId;
        private bool _carSpawned;
        private CarParameters _carParameters;
        private readonly List<IDisposable> _subs = new();

        public HeuristicLapScenario() : base(new TestScenarioDefinition(
            "ai-driver-heuristic-lap",
            "AI Driver — Heuristic Lap (Live)",
            "Pure-pursuit driver around a stadium loop (12 + 1 + 3 + 1 per side). " +
            "No ML. Validates loop detection → track query → kinematic car physics " +
            "end to end on a track with real straightaways and 90° corners.",
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
            SpawnCar();
            BuildHud();
            HookTickProxy();
            ApplyIsoCamera();

            Debug.Log($"[HeuristicLapScenario] Ready. Loop closed: {_loopService.IsLoopClosed}, car={_carId.Value}");
        }

        protected override ScenarioVerificationResult VerifyInternal(ScenarioParameterOverrides overrides)
        {
            var checks = new List<ScenarioVerificationResult.CheckResult>
            {
                new("loop closed",
                    _loopService != null && _loopService.IsLoopClosed,
                    "ClosedLoopService did not detect a loop after placement"),
                new("car spawned",
                    _carSpawned && _carSim.TryGetState(_carId, out _),
                    "no car was spawned"),
                new("track query active",
                    _trackQuery != null && _trackQuery.HasLoop,
                    "TrackQueryService reports no active loop"),
            };
            return new ScenarioVerificationResult(checks);
        }

        protected override void OnCleanup()
        {
            foreach (var s in _subs) s?.Dispose();
            _subs.Clear();

            if (_tickProxy != null) { _tickProxy.OnUpdate = null; _tickProxy.OnFixedUpdate = null; }
            _tickProxy = null;

            _carSim?.Dispose();
            _trackQuery?.Dispose();
            _loopService?.Dispose();
            _ribbonService?.Dispose();
            _placement?.Dispose();
            _factory?.Dispose();
            _eventBus?.ClearAllSubscriptions();

            _carSim = null; _trackQuery = null; _loopService = null;
            _ribbonService = null; _placement = null;
            _factory = null; _eventBus = null;

            DestroyAny(_terrainMesh); _terrainMesh = null;
            DestroyAny(_terrainMaterial); _terrainMaterial = null;

            _carVisual = null;
            _terrainObject = null;
            _hudSpeed = null; _hudLap = null; _hudLateral = null;
            _hudHeadingError = null; _hudBoost = null;
            _carSpawned = false;
        }

        // -------------------- build --------------------

        private void BuildTerrain()
        {
            _terrain = new TerrainService(_eventBus);
            _terrain.Initialize(new TerrainBuildOptions(TerrainWidth, TerrainDepth, 0, TrackPieceConstants.CellSize));

            _terrainMeshBuilder = new TerrainMeshBuilder();
            _terrainMesh = _terrainMeshBuilder.Build(_terrain, TerrainPalette.BoneAndSlate, TerrainColorMode.Palette);
            _terrainMaterial = ResolveOpaqueMaterial("AiDriver_HeuristicLap_TerrainMat");

            _terrainObject = _factory.CreateEmpty("[HeuristicLap] Terrain");
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

            _ribbonService = new TrackRibbonService(_eventBus, _placement, _pieceCatalog,
                _terrain, _factory, TrackPalette.Default);
        }

        private void BuildAiDriverServices()
        {
            _loopService = new ClosedLoopService(_eventBus, _placement, _pieceCatalog, _terrain);
            _trackQuery = new TrackQueryService(_eventBus, _loopService);
            _carSim = new CarSimulationService(_eventBus, _trackQuery);
        }

        private void BuildLoopGeometry()
        {
            // Stadium loop centred in the 20×20 terrain. Long sides = 12 straights
            // each, short sides = 3, four 90° curves at the corners. Total = 34
            // pieces. Curve_1x1 canonical (Facing North) ports are South + East;
            // yaw rotates ports CW per TrackDirection.YawDegrees:
            //   Facing East  → ports South + West   (NE corner)
            //   Facing South → ports North + West   (SE corner)
            //   Facing West  → ports North + East   (SW corner)
            //   Facing North → ports South + East   (NW corner)
            // Straight_1x1 facing East/West gives ports East + West (horizontal road);
            // facing North/South gives ports North + South (vertical road).
            const int xMin = 3;
            const int xMax = 16;
            const int zMin = 8;
            const int zMax = 12;

            // Bottom long side — 12 horizontal straights.
            for (int x = xMin + 1; x < xMax; x++)
                TryPlace(TrackPieceShapes.Straight_1x1, x, zMin, TrackDirection.East);
            // SE corner.
            TryPlace(TrackPieceShapes.Curve_1x1, xMax, zMin, TrackDirection.South);
            // Right short side — 3 vertical straights.
            for (int z = zMin + 1; z < zMax; z++)
                TryPlace(TrackPieceShapes.Straight_1x1, xMax, z, TrackDirection.North);
            // NE corner.
            TryPlace(TrackPieceShapes.Curve_1x1, xMax, zMax, TrackDirection.East);
            // Top long side — 12 horizontal straights.
            for (int x = xMin + 1; x < xMax; x++)
                TryPlace(TrackPieceShapes.Straight_1x1, x, zMax, TrackDirection.East);
            // NW corner.
            TryPlace(TrackPieceShapes.Curve_1x1, xMin, zMax, TrackDirection.North);
            // Left short side — 3 vertical straights.
            for (int z = zMin + 1; z < zMax; z++)
                TryPlace(TrackPieceShapes.Straight_1x1, xMin, z, TrackDirection.North);
            // SW corner — closes the loop.
            TryPlace(TrackPieceShapes.Curve_1x1, xMin, zMin, TrackDirection.West);
        }

        private void SpawnCar()
        {
            if (!_loopService.TryGetCurrentLoop(out var loop)) return;

            _carParameters = AiDriverPhysicsDefaults.Latest;
            // Single source of truth — see ClosedLoop.GetCanonicalStartPose.
            var pose = loop.GetCanonicalStartPose();
            Vector3 spawnPos = pose.position;
            float heading = loop.GetCanonicalStartHeading();

            // Same start-line + sector visualization the trainer uses, so the
            // gate the player sees here is byte-identical to the gate PPO sees.
            SectorBoundaryDebugRenderer.MountOn(SceneRoot, _loopService);

            _carId = _carSim.Spawn(spawnPos, heading, _carParameters);
            _carSpawned = true;

            _carVisual = _factory.CreatePrimitive(PrimitiveType.Cube, "[HeuristicLap] Car",
                spawnPos + new Vector3(0f, 0.1f, 0f));
            _carVisual.transform.SetParent(SceneRoot.transform, false);
            _carVisual.transform.localScale = new Vector3(0.25f, 0.18f, 0.4f);
            var renderer = _carVisual.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                var mat = ResolveOpaqueMaterial("AiDriver_HeuristicLap_CarMat");
                mat.color = CarColor;
                renderer.sharedMaterial = mat;
            }
        }

        private void BuildHud()
        {
            var root = RootVisualElement;
            var panel = new VisualElement
            {
                style =
                {
                    position = Position.Absolute,
                    left = 16, top = 16,
                    paddingLeft = 12, paddingRight = 12, paddingTop = 8, paddingBottom = 8,
                    backgroundColor = new Color(0f, 0f, 0f, 0.55f),
                    minWidth = 200,
                }
            };
            _hudSpeed = AppendLabel(panel, "speed: --");
            _hudLap = AppendLabel(panel, "lap: --");
            _hudLateral = AppendLabel(panel, "lateral: --");
            _hudHeadingError = AppendLabel(panel, "heading err: --");
            _hudBoost = AppendLabel(panel, "boost: --");
            root.Add(panel);
        }

        private static Label AppendLabel(VisualElement parent, string text)
        {
            var lbl = new Label(text)
            {
                style = { color = Color.white, fontSize = 14, marginBottom = 2 }
            };
            parent.Add(lbl);
            return lbl;
        }

        private void HookTickProxy()
        {
            var proxyGo = _factory.CreateEmpty("[HeuristicLap] TickProxy");
            proxyGo.transform.SetParent(SceneRoot.transform, false);
            _tickProxy = proxyGo.AddComponent<HeuristicLapTickProxy>();
            _tickProxy.OnUpdate = OnUpdate;
            _tickProxy.OnFixedUpdate = OnFixedUpdate;
        }

        private void ApplyIsoCamera()
        {
            var cam = Camera.main;
            if (cam == null) return;
            // Stadium centre sits roughly at terrain centre. High oblique angle so
            // both long sides + corners read clearly in one frame.
            float c = TrackPieceConstants.CellSize;
            var target = new Vector3(TerrainWidth * 0.5f * c, 0f, TerrainDepth * 0.5f * c);
            cam.transform.position = target + new Vector3(10f * c, 14f * c, -10f * c);
            cam.transform.LookAt(target);
        }

        // -------------------- tick --------------------

        private void OnUpdate(float deltaTime)
        {
            if (!_carSpawned) return;
            if (!_carSim.TryGetState(_carId, out var state)) return;

            // Pure-pursuit: sample one lookahead point, steer toward it.
            DriverInput input = ComputeHeuristicInput(state);
            _carSim.SetInput(_carId, input);

            UpdateCarVisual(state);
            UpdateHud(state, input);
        }

        private void OnFixedUpdate(float fixedDt)
        {
            _carSim?.FixedTick(fixedDt);
        }

        private DriverInput ComputeHeuristicInput(CarState state)
            => HeuristicDriver.Compute(state, _trackQuery, LookaheadMeters, SteerGain, MaxThrottle);

        private void UpdateCarVisual(CarState state)
        {
            if (_carVisual == null) return;
            _carVisual.transform.position = state.Position + new Vector3(0f, 0.1f, 0f);
            _carVisual.transform.rotation = Quaternion.Euler(0f, state.Heading * Mathf.Rad2Deg, 0f);
        }

        private void UpdateHud(CarState state, DriverInput input)
        {
            if (_hudSpeed == null) return;

            _hudSpeed.text = $"speed: {state.Speed:F2} m/s";
            _hudLap.text = $"lap dist: {state.LapDistance:F2} m";
            _hudBoost.text = $"boost: {state.BoostBudget * 100f:F0}%";

            if (_trackQuery.HasLoop)
            {
                var proj = _trackQuery.Project(state.Position, state.LastAnchorIndex);
                float headingErr = NormalizeAngle(
                    Mathf.Atan2(proj.Tangent.x, proj.Tangent.z) - state.Heading);
                _hudLateral.text = $"lateral: {proj.SignedLateralOffset:+0.00;-0.00} (hw {proj.HalfWidth:F2}){(proj.IsOffTrack ? " OFF" : "")}";
                _hudHeadingError.text = $"heading err: {headingErr * Mathf.Rad2Deg:+0.0;-0.0}°  steer={input.Steer:+0.00;-0.00}";
            }
        }

        // -------------------- helpers --------------------

        private void TryPlace(TrackPieceShape shape, int x, int z, TrackDirection facing)
        {
            var r = _placement.TryPlace(shape, new GridPosition(x, z), facing);
            if (!r.Success)
            {
                Debug.LogError($"[HeuristicLapScenario] placement failed: {shape.Id} @ ({x},{z}) face={facing}: {r.Reason}");
            }
        }

        private static float NormalizeAngle(float a)
        {
            while (a > Mathf.PI) a -= 2f * Mathf.PI;
            while (a < -Mathf.PI) a += 2f * Mathf.PI;
            return a;
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
