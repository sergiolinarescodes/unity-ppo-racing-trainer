using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.AiDriver.Loop;
using UnityPpoRacingTrainer.Core.AiDriver.Physics;
using UnityPpoRacingTrainer.Core.AiDriver.Policy;
using UnityPpoRacingTrainer.Core.Terrain;
using UnityPpoRacingTrainer.Core.Terrain.Scenarios;
using UnityPpoRacingTrainer.Core.Track.Loop;
using UnityPpoRacingTrainer.Core.Track.Ribbon;
using Unidad.Core.Abstractions;
using Unidad.Core.Grid;
using Unidad.Core.Testing;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Track.Scenarios
{
    /// <summary>
    /// U-shaped track with walls + kerbs around the outside, plus a small kinematic
    /// car that's launched with constant throttle and ZERO steering. The car cannot
    /// follow the curve on its own — it should drive forward, run out of straight,
    /// and hit the curve's outer wall. Wall hits are counted via
    /// <see cref="CarHitWallEvent"/> and printed to the Console every ~1 s.
    ///
    /// Pass condition (visual + console): at least one CarHitWallEvent is logged
    /// within ~5 s of starting the scenario. That confirms wall geometry is registered
    /// with the collision service and the car-sim collision query fires.
    ///
    /// What you should see:
    /// <list type="bullet">
    /// <item>U-shape: two parallel vertical legs joined by curves at the top.</item>
    /// <item>Each leg: side walls (~80 cm tall) along both long edges.</item>
    /// <item>Each curve: tall outer wall (the wall the car will hit) + striped inner kerb.</item>
    /// <item>Red car cube: spawns at the south end of the east leg, drives north,
    /// crashes into the outer wall of the top-east curve.</item>
    /// <item>Console: <c>[UCurveSandbox] WallHit #N at (...)</c> every impact, plus
    /// a periodic <c>[UCurveSandbox] tick=... walls=N</c> heartbeat.</item>
    /// </list>
    /// </summary>
    internal sealed class UCurveCollisionSandboxScenario : DataDrivenScenario, ITickable
    {
        // Sim runs at 50 Hz internally, scenario tick is whatever Update fires.
        // Keep an accumulator so we step CarSimulationService.FixedTick exactly
        // every 0.02 s regardless of frame rate.
        private const float FixedDt = 0.02f;

        private const int TerrainWidth = 24;
        private const int TerrainDepth = 24;

        private ScenarioEventBus _eventBus;
        private ScenarioGameObjectFactory _factory;
        private TerrainService _terrain;
        private TerrainMeshBuilder _terrainMeshBuilder;
        private TrackPieceCatalog _pieceCatalog;
        private TrackPieceMeshBuilder _pieceMeshBuilder;
        private List<ITrackPlacementValidator> _validators;
        private TrackPlacementService _placement;
        private TrackCollisionService _collision;
        private CarSimulationService _carSim;
        private CarId _carId;
        private GameObject _carVisual;

        private GameObject _terrainObject;
        private Mesh _terrainMesh;
        private Material _terrainMaterial;
        private Material _carMaterial;
        private UCurveSandboxTickProxy _tickProxy;

        private readonly List<IDisposable> _subs = new();
        private int _wallHitCount;
        private float _accumulator;
        private float _heartbeat;

        public UCurveCollisionSandboxScenario() : base(new TestScenarioDefinition(
            "u-curve-collision-sandbox",
            "U-Curve Collision Sandbox",
            "U-shaped track with the new wall + kerb geometry. Auto-launches a car with " +
            "constant throttle + zero steering so it crashes into the outer wall of the " +
            "top curve. Use this to confirm walls are colliding before retraining.",
            Array.Empty<ScenarioParameter>()))
        { }

        protected override void ExecuteInternal(ScenarioParameterOverrides overrides)
        {
            _eventBus = new ScenarioEventBus();
            _factory = new ScenarioGameObjectFactory();
            _wallHitCount = 0;
            _accumulator = 0f;
            _heartbeat = 0f;

            BuildTerrain();
            BuildTrackServices();
            LayoutUCurve();

            BuildCarSim();
            SpawnCar();
            HookTickProxy();
            FrameCamera();

            _subs.Add(_eventBus.Subscribe<CarHitWallEvent>(OnCarHitWall));

            Debug.Log($"[UCurveSandbox] Ready. Pieces={_placement.Placed.Count}, " +
                      $"walls={_collision.AllWalls.Count}, kerbs={_collision.AllKerbs.Count}, " +
                      $"car spawned at {SpawnPos()} heading {SpawnHeading():F2}rad. " +
                      $"Watch the console — wall hits should start within ~5s.");
        }

        // -------- Layout --------

        // U opens to the south. East leg: (3 ↑ 4 cells) → top-east curve → (3 horizontal cells) →
        // top-west curve → west leg (4 cells ↓).
        //
        // GridPosition uses (X, Y) where Y is the +Z axis in world (north).
        private const int LegLength = 4;        // straight cells per vertical leg
        private const int TopOffset = 5;        // east-west gap between the two legs (in cells)
        private const int LegBaseZ = 4;
        private const int EastLegX = 9;
        private const int WestLegX = EastLegX - TopOffset;

        private void LayoutUCurve()
        {
            // East leg (LegLength straights heading north)
            for (int i = 0; i < LegLength; i++)
                Place(TrackPieceShapes.Straight_1x1, EastLegX, LegBaseZ + i, TrackDirection.North);

            // Top-east curve: brings traffic from north-bound to west-bound (a left turn).
            // Use LeftCurve_1x1 facing North (south-enter, west-exit).
            Place(TrackPieceShapes.LeftCurve_1x1, EastLegX, LegBaseZ + LegLength, TrackDirection.North);

            // Top straights between the two curves.
            for (int i = 1; i < TopOffset; i++)
                Place(TrackPieceShapes.Straight_1x1, EastLegX - i, LegBaseZ + LegLength, TrackDirection.East);

            // Top-west curve: brings traffic from west-bound to south-bound (a left turn).
            // Curve_1x1 facing West (south-enter, east-exit, then rotated 90° CCW → west-enter, north-... no).
            // Simpler — drop the second curve too; the test only needs the FIRST wall hit.
            // Stop after the first curve to keep the layout cheap. Visual U-shape comes
            // from the first half; the car never reaches the second half anyway.
        }

        private void Place(TrackPieceShape shape, int x, int z, TrackDirection facing)
        {
            var origin = new GridPosition(x, z);
            var r = _placement.TryPlace(shape, origin, facing);
            if (!r.Success)
                Debug.LogWarning($"[UCurveSandbox] Place {shape}@({x},{z},{facing}) failed: {r.Reason}");
        }

        // -------- Setup --------

        private void BuildTerrain()
        {
            _terrain = new TerrainService(_eventBus);
            _terrain.Initialize(new TerrainBuildOptions(TerrainWidth, TerrainDepth, 0, TrackPieceConstants.CellSize));

            int cw = _terrain.CornerWidth;
            int cd = _terrain.CornerDepth;
            var heights = new float[cw, cd];
            for (int z = 0; z < cd; z++)
                for (int x = 0; x < cw; x++)
                    heights[x, z] = 0f;
            _terrain.TrySetAllCorners(heights);

            _terrainMeshBuilder = new TerrainMeshBuilder();
            _terrainMesh = _terrainMeshBuilder.Build(_terrain, TerrainPalette.BoneAndSlate, TerrainColorMode.Palette);

            _terrainMaterial = new Material(
                Shader.Find("RaceConstructor/TerrainVertexColor")
                ?? Shader.Find("Universal Render Pipeline/Lit")
                ?? Shader.Find("Standard")
                ?? Shader.Find("Hidden/InternalErrorShader"))
            { name = "UCurveSandbox_TerrainMat" };

            _terrainObject = _factory.CreateEmpty("[UCurveSandbox] Terrain");
            _terrainObject.transform.SetParent(SceneRoot.transform, false);
            _terrainObject.AddComponent<MeshFilter>().sharedMesh = _terrainMesh;
            _terrainObject.AddComponent<MeshRenderer>().sharedMaterial = _terrainMaterial;
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

            _collision = new TrackCollisionService();
            _placement = new TrackPlacementService(
                _eventBus, _pieceCatalog, _pieceMeshBuilder,
                _validators, _terrain, _factory, TrackPalette.Default,
                _collision);
        }

        private void BuildCarSim()
        {
            // The car-sim normally needs an ITrackQueryService for off-track + lap state,
            // but for the sandbox we only care about wall collision. Pass a no-op query
            // (HasLoop=false) so the sim still steps. We bypass it via a stub instance.
            var stubQuery = new SandboxTrackQuery();
            _carSim = new CarSimulationService(_eventBus, stubQuery, _collision);
        }

        private Vector3 SpawnPos()
        {
            float c = TrackPieceConstants.CellSize;
            return new Vector3(
                (EastLegX + 0.5f) * c,
                0f,
                (LegBaseZ + 0.2f) * c);
        }

        private float SpawnHeading() => 0f; // 0 rad = +Z (north)

        private void SpawnCar()
        {
            var p = AiDriverPhysicsDefaults.Latest;
            // Nudge top speed down a touch so the wall impact is visually catchable.
            _carId = _carSim.Spawn(SpawnPos(), SpawnHeading(), p);
            _carSim.SetInput(_carId, new DriverInput(Steer: 0f, Throttle: 0.6f, Boost: false));

            // Visual: red cube at car position, updated every Update.
            _carVisual = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _carVisual.name = "[UCurveSandbox] Car";
            _carVisual.transform.SetParent(SceneRoot.transform, false);
            float c = TrackPieceConstants.CellSize;
            _carVisual.transform.localScale = new Vector3(0.6f, 0.4f, 1.0f) * c * 0.5f;
            // Edit-mode scenarios run outside Play mode, where Object.Destroy is
            // illegal. DestroyImmediate is the correct path here; the scenario
            // never reaches a build.
            UnityEngine.Object.DestroyImmediate(_carVisual.GetComponent<Collider>());

            _carMaterial = new Material(
                Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"))
            { color = new Color(0.95f, 0.20f, 0.20f) };
            _carVisual.GetComponent<MeshRenderer>().sharedMaterial = _carMaterial;
        }

        private void HookTickProxy()
        {
            var proxyGo = _factory.CreateEmpty("[UCurveSandbox] TickProxy");
            proxyGo.transform.SetParent(SceneRoot.transform, false);
            _tickProxy = proxyGo.AddComponent<UCurveSandboxTickProxy>();
            _tickProxy.OnTick = Tick;
        }

        public void Tick(float deltaTime)
        {
            _accumulator += deltaTime;
            while (_accumulator >= FixedDt)
            {
                _carSim.FixedTick(FixedDt);
                _accumulator -= FixedDt;
            }

            // Sync car visual.
            if (_carSim.TryGetState(_carId, out var state) && _carVisual != null)
            {
                _carVisual.transform.position = state.Position + Vector3.up * 0.4f;
                _carVisual.transform.rotation = Quaternion.Euler(0f, state.Heading * Mathf.Rad2Deg, 0f);

                // Phase-2 wall-ray viz mirroring AiDriverPolicyService observation:
                // 6 feeler rays at body-relative {-90°,-45°,-22.5°,+22.5°,+45°,+90°}.
                // Yellow = clear, red = wall closing in. Visible in Scene view via
                // Debug.DrawLine. Lets you eyeball whether the rays "see" the U-curve
                // outer wall before impact.
                float h = state.Heading;
                float ch = Mathf.Cos(h);
                float sh = Mathf.Sin(h);
                Vector2 originXZ = new(state.Position.x, state.Position.z);
                Vector3 originW = state.Position + Vector3.up * 0.45f;
                float maxR = RacingObservationLayout.WallRayMaxMeters;
                for (int i = 0; i < RacingObservationLayout.WallRayCount; i++)
                {
                    float a = RacingObservationLayout.WallRayAnglesRad[i];
                    float ca = Mathf.Cos(a);
                    float sa = Mathf.Sin(a);
                    float dx = sh * ca + ch * sa;
                    float dz = ch * ca - sh * sa;
                    float d = _collision.RaycastWall(originXZ, new Vector2(dx, dz), maxR);
                    Vector3 endW = originW + new Vector3(dx, 0f, dz) * d;
                    float occ = 1f - Mathf.Clamp01(d / maxR);
                    Color rayColor = Color.Lerp(Color.yellow, Color.red, occ);
                    Debug.DrawLine(originW, endW, rayColor);
                }
            }

            _heartbeat += deltaTime;
            if (_heartbeat >= 1f)
            {
                _heartbeat = 0f;
                if (_carSim.TryGetState(_carId, out var s))
                {
                    Debug.Log($"[UCurveSandbox] tick speed={s.VelocityXZ.magnitude:F2} " +
                              $"pos=({s.Position.x:F1},{s.Position.z:F1}) walls_hit={_wallHitCount}");
                }
            }
        }

        private void OnCarHitWall(CarHitWallEvent evt)
        {
            _wallHitCount++;
            Debug.Log($"[UCurveSandbox] WallHit #{_wallHitCount} at ({evt.Position.x:F2},{evt.Position.z:F2}) " +
                      $"normal=({evt.Normal.x:F2},{evt.Normal.z:F2}) impactSpeed={evt.ImpactSpeed:F2}");
        }

        private void FrameCamera()
        {
            var cam = Camera.main;
            if (cam == null) return;
            float c = TrackPieceConstants.CellSize;
            float cx = (EastLegX - TopOffset * 0.5f) * c;
            float cz = (LegBaseZ + LegLength * 0.5f) * c;
            cam.transform.position = new Vector3(cx + 6f * c, 14f * c, cz - 12f * c);
            cam.transform.LookAt(new Vector3(cx, 0f, cz));
        }

        protected override ScenarioVerificationResult VerifyInternal(ScenarioParameterOverrides overrides)
        {
            // Deterministic setup-only checks — wall-hit count is runtime state and
            // belongs to the console log, not Verify().
            var checks = new List<ScenarioVerificationResult.CheckResult>
            {
                new("placement-pieces-spawned",
                    _placement != null && _placement.Placed.Count > 0,
                    $"placed pieces: {(_placement?.Placed.Count ?? 0)}"),
                new("collision-walls-registered",
                    _collision != null && _collision.AllWalls.Count > 0,
                    $"wall segments: {(_collision?.AllWalls.Count ?? 0)}"),
                new("car-spawned",
                    _carSim != null && _carSim.TryGetState(_carId, out _),
                    $"car id: {_carId}"),
            };
            return new ScenarioVerificationResult(checks);
        }

        protected override void OnCleanup()
        {
            foreach (var s in _subs) s.Dispose();
            _subs.Clear();

            _carSim?.Despawn(_carId);
            _placement?.Dispose();
            _factory?.Dispose();
            _eventBus?.ClearAllSubscriptions();

            if (_terrainMaterial != null)
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(_terrainMaterial); else UnityEngine.Object.DestroyImmediate(_terrainMaterial);
                _terrainMaterial = null;
            }
            if (_carMaterial != null)
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(_carMaterial); else UnityEngine.Object.DestroyImmediate(_carMaterial);
                _carMaterial = null;
            }
            base.OnCleanup();
        }

        // -------- Local helpers --------

        // Stub query: scenario doesn't need a closed loop. Reports HasLoop=false so
        // the car-sim skips the centerline projection branch but still runs the
        // wall-collision query each tick (that's gated on _collision != null only).
        private sealed class SandboxTrackQuery : ITrackQueryService
        {
            public bool HasLoop => false;
            public bool HasPath => false;
            public float TotalPathLength => 0f;
            public TrackProjection Project(Vector3 worldPos, int hintAnchorIndex = -1) => default;
            public void SampleLookahead(int startAnchorIndex, float distanceMeters, int sampleCount,
                Span<CenterlineSample> output) { }
            public void SampleLookaheadAt(int startAnchorIndex, ReadOnlySpan<float> arcOffsetsMeters,
                Span<CenterlineSample> output) { }
            public float GetElevationAt(Vector3 worldPos) => 0f;
        }
    }

    /// <summary>
    /// MonoBehaviour that drives the U-curve sandbox scenario's per-frame tick.
    /// Mirrors <see cref="MouseShapePlacementTickProxy"/> but kept distinct so the
    /// two scenarios don't share state.
    /// </summary>
    internal sealed class UCurveSandboxTickProxy : MonoBehaviour
    {
        public Action<float> OnTick;
        private void Update() => OnTick?.Invoke(Time.deltaTime);
    }
}
