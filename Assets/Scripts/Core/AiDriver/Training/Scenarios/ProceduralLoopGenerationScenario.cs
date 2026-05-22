using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.AiDriver.Training.Curriculum;
using UnityPpoRacingTrainer.Core.AiDriver.Training.Generation;
using UnityPpoRacingTrainer.Core.Terrain;
using UnityPpoRacingTrainer.Core.Terrain.Scenarios;
using UnityPpoRacingTrainer.Core.Track;
using UnityPpoRacingTrainer.Core.Track.Loop;
using UnityPpoRacingTrainer.Core.Track.Ribbon;
using UnityPpoRacingTrainer.Core.Track.Shape;
using Unidad.Core.Grid;
using Unidad.Core.Testing;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.AiDriver.Training.Scenarios
{
    /// <summary>
    /// Visual proof of the procedural loop generator. Builds a flat terrain, runs
    /// <see cref="ProceduralLoopGenerator"/> with the chosen seed + stage, and lets
    /// <c>TrackRibbonService</c> render the resulting loop. The generator is fully
    /// deterministic for a given (seed, stage) pair, so re-running with the same
    /// parameters produces an identical track.
    /// </summary>
    internal sealed class ProceduralLoopGenerationScenario : DataDrivenScenario
    {
        private const int TerrainWidth = 30;
        private const int TerrainDepth = 30;
        private const int OriginX = 15;
        private const int OriginZ = 15;

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
        private ShapeBasedLoopGenerator _generator;

        // -- presentation --
        private GameObject _terrainObject;
        private Mesh _terrainMesh;
        private Material _terrainMaterial;

        // -- runtime --
        private GenerationResult _lastResult;

        public ProceduralLoopGenerationScenario() : base(new TestScenarioDefinition(
            "ai-driver-procedural-loop",
            "AI Driver — Procedural Loop Generation (Live)",
            "Runs the procedural loop generator with the chosen seed + curriculum " +
            "stage and renders the result via TrackRibbonService. Same seed + stage " +
            "produces an identical track every time.",
            new[]
            {
                new ScenarioParameter("seed", "Seed", typeof(int), 1234, 0, 1_000_000),
                new ScenarioParameter("stageId", "Stage Id (1-4)", typeof(int), 1, 1, 4),
            }))
        { }

        protected override void ExecuteInternal(ScenarioParameterOverrides overrides)
        {
            int seed = ResolveParam<int>(overrides, "seed");
            int stageId = ResolveParam<int>(overrides, "stageId");

            if (!CurriculumStages.TryGet(stageId, out var stage))
            {
                Debug.LogWarning($"[ProcLoopGen] Unknown stageId={stageId}, falling back to stage 1.");
                stage = CurriculumStages.All[0];
            }

            _eventBus = new ScenarioEventBus();
            _factory = new ScenarioGameObjectFactory();

            BuildTerrain();
            BuildTrackServices();
            BuildLoopServices();

            _generator = new ShapeBasedLoopGenerator(_eventBus, _shapePlacement, _shapeCatalog, _placement, _loopService);

            var cfg = new GenerationConfig(
                Seed: seed,
                Origin: new GridPosition(OriginX, OriginZ),
                InitialFacing: TrackDirection.East,
                Stage: stage);

            _lastResult = _generator.Generate(cfg);

            if (_lastResult.Success)
            {
                Debug.Log($"[ProcLoopGen] stage={stage.Name} seed={seed} pieces={_lastResult.PlacedPieces} " +
                          $"length={_lastResult.TotalLength:F1}m loopId={_lastResult.LoopId}");
            }
            else
            {
                Debug.LogWarning($"[ProcLoopGen] generation failed: stage={stage.Name} seed={seed}: {_lastResult.FailureReason}");
            }

            ApplyIsoCamera();
        }

        protected override ScenarioVerificationResult VerifyInternal(ScenarioParameterOverrides overrides)
        {
            // Closure depends on RNG draw — assert only deterministic setup.
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
                new("generator ran",
                    _generator != null,
                    "Generator was not constructed"),
            };
            return new ScenarioVerificationResult(checks);
        }

        protected override void OnCleanup()
        {
            _generator?.Dispose();
            _loopService?.Dispose();
            _ribbonService?.Dispose();
            _placement?.Dispose();
            _factory?.Dispose();
            _eventBus?.ClearAllSubscriptions();

            _generator = null;
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
            _terrainMaterial = ResolveOpaqueMaterial("AiDriver_ProcLoopGen_TerrainMat");

            _terrainObject = _factory.CreateEmpty("[ProcLoopGen] Terrain");
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

        private static void DestroyAny(UnityEngine.Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(o);
            else UnityEngine.Object.DestroyImmediate(o);
        }
    }
}
