using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.Terrain;
using UnityPpoRacingTrainer.Core.Terrain.Generators;
using UnityPpoRacingTrainer.Core.Terrain.Scenarios;
using Unidad.Core.Grid;
using Unidad.Core.Testing;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Track.Scenarios
{
    /// <summary>
    /// Visual smoke test for the wall mesh pipeline. Lays one of each catalog piece
    /// on a flat terrain in a 4×3 grid, with each piece getting its AutoBarriers
    /// default (Straights → side walls; Curves → outer wall only). Static kerbs were
    /// removed — kerbs are placed dynamically by the racing-line kerb service in
    /// the ghost-loop preview, not by this scenario.
    /// </summary>
    internal sealed class WallKerbPieceShowcaseScenario : DataDrivenScenario
    {
        private const int TerrainWidth = 24;
        private const int TerrainDepth = 18;

        private ScenarioEventBus _eventBus;
        private ScenarioGameObjectFactory _factory;
        private TerrainService _terrain;
        private TerrainMeshBuilder _terrainMeshBuilder;
        private TrackPieceCatalog _pieceCatalog;
        private TrackPieceMeshBuilder _pieceMeshBuilder;
        private List<ITrackPlacementValidator> _validators;
        private TrackPlacementService _placement;
        private TrackCollisionService _collision;

        private GameObject _terrainObject;
        private Material _terrainMaterial;
        private Mesh _terrainMesh;

        public WallKerbPieceShowcaseScenario() : base(new TestScenarioDefinition(
            "wall-piece-showcase",
            "Wall Piece Showcase",
            "Lays one of each Straight / Curve / Diagonal / Ramp piece on a flat terrain " +
            "to visually verify the wall geometry. Straights get side walls; " +
            "curves get an outer wall only. Static kerbs are gone — see the racing-line " +
            "kerb scenarios for the dynamic replacement. No simulation.",
            Array.Empty<ScenarioParameter>()))
        { }

        protected override void ExecuteInternal(ScenarioParameterOverrides overrides)
        {
            _eventBus = new ScenarioEventBus();
            _factory = new ScenarioGameObjectFactory();

            BuildTerrain();
            BuildTrackServices();
            LayoutPieces();

            FrameCamera();

            Debug.Log($"[WallShowcase] Placed {_placement.Placed.Count} pieces. " +
                      $"Walls registered: {_collision.AllWalls.Count}.");
        }

        private void BuildTerrain()
        {
            _terrain = new TerrainService(_eventBus);
            _terrain.Initialize(new TerrainBuildOptions(TerrainWidth, TerrainDepth, 0, TrackPieceConstants.CellSize));

            int cw = _terrain.CornerWidth;
            int cd = _terrain.CornerDepth;
            var heights = new float[cw, cd];
            // Flat — wall visuals are easier to compare without slope shadow.
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
            { name = "WallShowcase_TerrainMaterial" };

            _terrainObject = _factory.CreateEmpty("[WallShowcase] Terrain");
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

        private void LayoutPieces()
        {
            // 4×3 grid of slots, ~3 cells apart so each piece has clear airspace.
            // x = column position, z = row position; rows top→bottom.
            // Choosing one piece per slot: showcase the catalog one canonical orientation each.
            var slots = new[]
            {
                (col: 0, row: 0, shape: TrackPieceShapes.Straight_1x1, facing: TrackDirection.North),
                (col: 1, row: 0, shape: TrackPieceShapes.Straight_1x2, facing: TrackDirection.North),
                (col: 2, row: 0, shape: TrackPieceShapes.Curve_1x1, facing: TrackDirection.North),
                (col: 3, row: 0, shape: TrackPieceShapes.LeftCurve_1x1, facing: TrackDirection.North),

                (col: 0, row: 1, shape: TrackPieceShapes.Curve_Long_1x2, facing: TrackDirection.North),
                (col: 1, row: 1, shape: TrackPieceShapes.Straight_Diag_1x1, facing: TrackDirection.North),
                (col: 2, row: 1, shape: TrackPieceShapes.CurveDiagTransition_1x1, facing: TrackDirection.North),
                (col: 3, row: 1, shape: TrackPieceShapes.CurveDiagTransitionLeft_1x1, facing: TrackDirection.North),

                (col: 0, row: 2, shape: TrackPieceShapes.CurveDiagToCardinal_1x1, facing: TrackDirection.North),
                (col: 1, row: 2, shape: TrackPieceShapes.CurveDiagToCardinalLeft_1x1, facing: TrackDirection.North),
                (col: 2, row: 2, shape: TrackPieceShapes.CurveDiagHairpin_1x1, facing: TrackDirection.North),
                (col: 3, row: 2, shape: TrackPieceShapes.Ramp_1x1, facing: TrackDirection.North),
            };

            const int colSpacing = 5;
            const int rowSpacing = 5;
            int xBase = 2;
            int zBase = 2;

            foreach (var slot in slots)
            {
                var origin = new GridPosition(
                    xBase + slot.col * colSpacing,
                    zBase + slot.row * rowSpacing);
                var result = _placement.TryPlace(slot.shape, origin, slot.facing);
                if (!result.Success)
                {
                    Debug.LogWarning($"[WallShowcase] could not place {slot.shape}: {result.Reason}");
                }
            }
        }

        private void FrameCamera()
        {
            var cam = Camera.main;
            if (cam == null) return;
            float c = TrackPieceConstants.CellSize;
            float cx = TerrainWidth * 0.5f * c;
            float cz = TerrainDepth * 0.5f * c;
            cam.transform.position = new Vector3(cx + 8f * c, 16f * c, cz - 14f * c);
            cam.transform.LookAt(new Vector3(cx, 0f, cz));
        }

        protected override ScenarioVerificationResult VerifyInternal(ScenarioParameterOverrides overrides)
        {
            // Deterministic setup-only checks — never assert on runtime state per
            // the project's scenario-test rules (Verify() must always pass at run time).
            var checks = new List<ScenarioVerificationResult.CheckResult>
            {
                new("placement-pieces-spawned",
                    _placement != null && _placement.Placed.Count > 0,
                    $"placed pieces: {(_placement?.Placed.Count ?? 0)}"),
                new("collision-walls-registered",
                    _collision != null && _collision.AllWalls.Count > 0,
                    $"wall segments: {(_collision?.AllWalls.Count ?? 0)}"),
            };
            return new ScenarioVerificationResult(checks);
        }

        protected override void OnCleanup()
        {
            _placement?.Dispose();
            _factory?.Dispose();
            _eventBus?.ClearAllSubscriptions();
            if (_terrainMaterial != null)
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(_terrainMaterial);
                else UnityEngine.Object.DestroyImmediate(_terrainMaterial);
                _terrainMaterial = null;
            }
            base.OnCleanup();
        }
    }
}
