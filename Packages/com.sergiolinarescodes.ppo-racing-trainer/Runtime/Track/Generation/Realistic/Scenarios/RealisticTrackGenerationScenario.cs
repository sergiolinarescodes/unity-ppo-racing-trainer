using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.AiDriver.Training.Curriculum;
using UnityPpoRacingTrainer.Core.AiDriver.Training.Generation;
using UnityPpoRacingTrainer.Core.Terrain;
using UnityPpoRacingTrainer.Core.Terrain.Scenarios;
using UnityPpoRacingTrainer.Core.Track.Generation.Realistic.Native;
using UnityPpoRacingTrainer.Core.Track.Loop;
using UnityPpoRacingTrainer.Core.Track.Ribbon;
using UnityPpoRacingTrainer.Core.Track.Shape;
using Unidad.Core.Grid;
using Unidad.Core.Testing;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Track.Generation.Realistic.Scenarios
{
    /// <summary>
    /// Visual proof of the F1-flavoured generator. Builds a flat training-sized
    /// terrain, runs <see cref="RealisticTrackGenerator"/> with the chosen seed +
    /// stage, and lets the existing ribbon mesh service render the result. Same
    /// (seed, stage) pair → identical track every call.
    /// </summary>
    internal sealed class RealisticTrackGenerationScenario : DataDrivenScenario
    {
        private const int TerrainWidth = 60;
        private const int TerrainDepth = 60;
        private const int OriginX = 12;
        private const int OriginZ = 30;

        // -- service graph --
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
        private TrackShapeCatalog _shapeCatalog;
        private ShapePreviewService _shapePreview;
        private ShapePlacementService _shapePlacement;
        private RealisticNativeCatalog _native;
        private RealisticTrackGenerator _generator;

        // -- presentation --
        private GameObject _terrainObject;
        private Mesh _terrainMesh;
        private Material _terrainMaterial;

        // -- runtime --
        private GenerationResult _lastResult;

        public RealisticTrackGenerationScenario() : base(new TestScenarioDefinition(
            "ai-driver-realistic-loop",
            "AI Driver — Realistic F1-Style Loop (Live)",
            "Runs the F1-flavoured generator with the chosen seed and renders " +
            "the result via TrackRibbonService. Same seed → identical track.",
            new[]
            {
                new ScenarioParameter("seed", "Seed", typeof(int), 1234, 0, 1_000_000),
                new ScenarioParameter("allowDiagonals", "Allow Diagonal Sweeps", typeof(bool), true),
                new ScenarioParameter("targetLengthCells", "Target Length (cells)", typeof(int), 60, 20, 200),
                new ScenarioParameter("turnDensity", "Turn Density (0..1)", typeof(float), 0.4f, 0f, 1f),
                new ScenarioParameter("beamWidth", "Beam Width", typeof(int), 16, 1, 64),
                new ScenarioParameter("maxSearchSteps", "Max Search Steps", typeof(int), 32, 4, 128),
                new ScenarioParameter("attempts", "Attempts (RecipeAttempts)", typeof(int), 6, 1, 16),
                new ScenarioParameter("timeoutMs", "Per-attempt timeout (ms)", typeof(int), 1500, 100, 10000),
            }))
        { }

        protected override void ExecuteInternal(ScenarioParameterOverrides overrides)
        {
            int seed = ResolveParam<int>(overrides, "seed");
            bool allowDiagonals = ResolveParam<bool>(overrides, "allowDiagonals");
            int targetLengthCells = ResolveParam<int>(overrides, "targetLengthCells");
            float turnDensity = ResolveParam<float>(overrides, "turnDensity");
            int beamWidth = ResolveParam<int>(overrides, "beamWidth");
            int maxSearchSteps = ResolveParam<int>(overrides, "maxSearchSteps");
            int attempts = ResolveParam<int>(overrides, "attempts");
            int timeoutMs = ResolveParam<int>(overrides, "timeoutMs");

            var stage = CurriculumStages.Default;

            _eventBus = new ScenarioEventBus();
            _factory = new ScenarioGameObjectFactory();

            BuildTerrain();
            BuildTrackServices();
            BuildLoopServices();

            _native = new RealisticNativeCatalog(_shapeCatalog, _pieceCatalog);
            _generator = new RealisticTrackGenerator(
                _eventBus, _placement, _shapePlacement, _loopService,
                _shapeCatalog, _pieceCatalog, _terrain, _native);

            var cfg = new RealisticTrackGenerationConfig(
                Seed: seed,
                Origin: new GridPosition(OriginX, OriginZ),
                InitialFacing: TrackDirection.East,
                Stage: stage,
                RecipeAttempts: attempts,
                AllowDiagonalSweeps: allowDiagonals,
                TargetLengthCells: targetLengthCells,
                TurnDensity: turnDensity,
                BeamWidth: beamWidth,
                MaxSearchSteps: maxSearchSteps,
                PerAttemptTimeoutMs: timeoutMs);

            _lastResult = _generator.Generate(cfg);

            // ALWAYS produce a circuit. If the realistic generator fails on
            // every attempt (tight crossing/leg constraints) fall back to the
            // recipe-based ShapeBased generator — same contract the curriculum
            // selector uses for training. Never leave the user with no track.
            if (!_lastResult.Success)
            {
                Debug.LogWarning($"[RealTrackGen] realistic generation failed: stage={stage.Name} " +
                                 $"seed={seed}: {_lastResult.FailureReason} — falling back to ShapeBased");
                _placement.Clear();
                var shapeBased = new ShapeBasedLoopGenerator(_eventBus, _shapePlacement,
                    _shapeCatalog, _placement, _loopService);
                _lastResult = shapeBased.Generate(new GenerationConfig(seed,
                    new GridPosition(OriginX, OriginZ), TrackDirection.East, stage));
            }

            if (_lastResult.Success)
            {
                Debug.Log($"[RealTrackGen] stage={stage.Name} seed={seed} " +
                          $"pieces={_lastResult.PlacedPieces} length={_lastResult.TotalLength:F1}m " +
                          $"loopId={_lastResult.LoopId}");
            }
            else
            {
                Debug.LogWarning($"[RealTrackGen] BOTH generators failed: stage={stage.Name} " +
                                 $"seed={seed}: {_lastResult.FailureReason}");
            }

            // Now build the ribbon mesh from the placed pieces — single rebuild,
            // not one per piece commit.
            _ribbonService = new TrackRibbonService(_eventBus, _placement, _pieceCatalog,
                _terrain, _factory, TrackPalette.Default);
            _ribbonService.Rebuild();

            // Draw a clearly visual start/finish line at the lap start, oriented
            // perpendicular to the track tangent. The ML agent spawns in front
            // of this line and progresses around the loop in tangent direction;
            // the lap counter (TrackQueryService) projects to arc length so cuts
            // (off-track shortcuts) cause arc length to drop, not progress.
            if (_lastResult.Success && _loopService.TryGetCurrentLoop(out var loop))
            {
                BuildStartFinishMarker(loop.LapStartPosition, loop.LapStartTangent);
                // Same K=9 sector posts + start gate the trainer renders.
                SectorBoundaryDebugRenderer.MountOn(SceneRoot, _loopService);
            }

            ApplyIsoCamera();
        }

        private GameObject _startFinishMarker;
        private Mesh _startFinishMesh;
        private Material _startFinishMat;

        private void BuildStartFinishMarker(Vector3 startPos, Vector3 tangent)
        {
            float c = TrackPieceConstants.CellSize;
            float halfWidth = TrackPieceConstants.LaneHalfWidth * c * 1.6f;
            float thickness = 0.18f * c;
            float height = TrackPieceConstants.SlabTopY + TrackPieceConstants.RoadLift + 0.005f;

            // Lay a checkered ribbon perpendicular to the chain tangent.
            Vector3 t = new Vector3(tangent.x, 0f, tangent.z);
            if (t.sqrMagnitude < 1e-6f) t = Vector3.forward;
            t.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, t).normalized;

            Vector3 p = startPos + new Vector3(0f, height, 0f);
            Vector3 a = p - right * halfWidth - t * thickness;
            Vector3 b = p + right * halfWidth - t * thickness;
            Vector3 cc = p + right * halfWidth + t * thickness;
            Vector3 d = p - right * halfWidth + t * thickness;

            var verts = new[] { a, b, cc, d };
            var tris = new[] { 0, 1, 2, 0, 2, 3 };
            var colors = new[] { Color.red, Color.white, Color.red, Color.white };
            _startFinishMesh = new Mesh
            {
                name = "StartFinishLine",
                vertices = verts,
                triangles = tris,
                colors = colors,
            };
            _startFinishMesh.RecalculateNormals();
            _startFinishMesh.RecalculateBounds();

            _startFinishMat = ResolveOpaqueMaterial("StartFinish_Mat");
            _startFinishMarker = _factory.CreateEmpty("[RealTrackGen] StartFinishLine");
            _startFinishMarker.transform.SetParent(SceneRoot.transform, false);
            var mf = _startFinishMarker.AddComponent<MeshFilter>();
            mf.sharedMesh = _startFinishMesh;
            var mr = _startFinishMarker.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _startFinishMat;
        }

        protected override ScenarioVerificationResult VerifyInternal(ScenarioParameterOverrides overrides)
        {
            var checks = new List<ScenarioVerificationResult.CheckResult>
            {
                new("terrain initialised",
                    _terrain != null && _terrain.IsInitialized,
                    "Terrain was not initialised"),
                new("placement service ready",
                    _placement != null,
                    "TrackPlacementService missing"),
                new("loop service ready",
                    _loopService != null,
                    "ClosedLoopService missing"),
                new("realistic generator ran",
                    _generator != null,
                    "RealisticTrackGenerator was not constructed"),
            };
            return new ScenarioVerificationResult(checks);
        }

        protected override void OnCleanup()
        {
            _generator?.Dispose();
            _generator = null;
            _native?.Dispose();
            _native = null;
            _loopService?.Dispose();
            _ribbonService?.Dispose();
            _placement?.Dispose();
            _factory?.Dispose();
            _eventBus?.ClearAllSubscriptions();

            _loopService = null;
            _ribbonService = null;
            _placement = null;
            _validators = null;
            _pieceMeshBuilder = null;
            _pieceCatalog = null;
            _terrain = null;
            _factory = null;
            _eventBus = null;

            DestroyAny(_terrainMesh); _terrainMesh = null;
            DestroyAny(_terrainMaterial); _terrainMaterial = null;
            DestroyAny(_startFinishMesh); _startFinishMesh = null;
            DestroyAny(_startFinishMat); _startFinishMat = null;
            _startFinishMarker = null;
            _terrainObject = null;
            _terrainMeshBuilder = null;
            _lastResult = default;
        }

        // -------------------- build --------------------

        private void BuildTerrain()
        {
            _terrain = new TerrainService(_eventBus);
            _terrain.Initialize(new TerrainBuildOptions(TerrainWidth, TerrainDepth, 0, TrackPieceConstants.CellSize));

            _terrainMeshBuilder = new TerrainMeshBuilder();
            _terrainMesh = _terrainMeshBuilder.Build(_terrain, TerrainPalette.BoneAndSlate, TerrainColorMode.Palette);
            _terrainMaterial = ResolveOpaqueMaterial("RealTrackGen_TerrainMat");

            _terrainObject = _factory.CreateEmpty("[RealTrackGen] Terrain");
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

            // Ribbon is constructed AFTER generation so per-piece placement events
            // don't trigger a full ribbon rebuild on every commit (each rebuild is
            // O(placed^2) and the generator can place 30-90 pieces per attempt
            // across 6+ attempts — without this the search blows the timeout).
        }

        private void BuildLoopServices()
        {
            _loopService = new ClosedLoopService(_eventBus, _placement, _pieceCatalog, _terrain);

            _shapeCatalog = new TrackShapeCatalog();
            TrackShapeCatalogSeeder.Seed(_shapeCatalog, _pieceCatalog);
            _shapePreview = new ShapePreviewService(_pieceCatalog, _validators, _terrain, _placement);
            _shapePlacement = new ShapePlacementService(_eventBus, _shapePreview, _placement);
        }

        private void ApplyIsoCamera()
        {
            var cam = Camera.main;
            if (cam == null) return;
            float c = TrackPieceConstants.CellSize;
            var target = new Vector3(TerrainWidth * 0.5f * c, 0f, TerrainDepth * 0.5f * c);
            cam.transform.position = target + new Vector3(15f * c, 22f * c, -15f * c);
            cam.transform.LookAt(target);
        }

        // -------------------- helpers --------------------

        private Material ResolveOpaqueMaterial(string name)
        {
            var shader = Shader.Find("RaceConstructor/TerrainVertexColor")
                         ?? Shader.Find("Universal Render Pipeline/Lit")
                         ?? Shader.Find("Standard");
            return new Material(shader) { name = name };
        }

        private static void DestroyAny(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Object.Destroy(o);
            else Object.DestroyImmediate(o);
        }
    }
}
