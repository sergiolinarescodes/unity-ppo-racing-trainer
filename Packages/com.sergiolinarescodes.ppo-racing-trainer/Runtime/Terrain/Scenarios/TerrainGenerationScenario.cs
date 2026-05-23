using System;
using System.Collections.Generic;
using Unidad.Core.EventBus;
using Unidad.Core.Testing;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Terrain.Scenarios
{
    internal sealed class TerrainGenerationScenario : DataDrivenScenario
    {
        private static readonly ScenarioParameter WidthParam = new(
            "width", "Width", typeof(int), 8, 2, 32);
        private static readonly ScenarioParameter DepthParam = new(
            "depth", "Depth", typeof(int), 8, 2, 32);
        private static readonly ScenarioParameter SeedParam = new(
            "randomSeed", "Random Seed", typeof(int), 1337, 0, 99999);
        private static readonly ScenarioParameter AllowRampsParam = new(
            "allowRamps", "Allow Ramps", typeof(bool), true);
        private static readonly ScenarioParameter MaxLevelParam = new(
            "maxHeightLevel", "Max Height Level", typeof(int), 3, 0, 6);
        // Default = global TrackPieceConstants.CellSize so the live terrain matches
        // every other scenario unless the user explicitly overrides via parameter.
        private static readonly ScenarioParameter CellSizeParam = new(
            "cellSize", "Cell Size", typeof(float),
            Track.TrackPieceConstants.CellSize, 0.5f, 4f);

        private IEventBus _eventBus;
        private TerrainService _service;
        private TerrainMeshBuilder _builder;
        private GameObject _meshObject;
        private Mesh _mesh;
        private Material _material;
        private readonly List<IDisposable> _subscriptions = new();
        private int _width;
        private int _depth;

        public TerrainGenerationScenario() : base(new TestScenarioDefinition(
            "terrain-generation",
            "Terrain Generation (Live)",
            "Procedurally generates a seeded terrain with flats and 4-direction ramps. " +
            "Spawns the procedural mesh in the scene. Logs terrain events to the Console.",
            new[] { WidthParam, DepthParam, SeedParam, AllowRampsParam, MaxLevelParam, CellSizeParam }))
        { }

        protected override void ExecuteInternal(ScenarioParameterOverrides overrides)
        {
            _width = Mathf.Clamp(ResolveParam<int>(overrides, "width"), 2, 32);
            _depth = Mathf.Clamp(ResolveParam<int>(overrides, "depth"), 2, 32);
            int seed = ResolveParam<int>(overrides, "randomSeed");
            bool allowRamps = ResolveParam<bool>(overrides, "allowRamps");
            int maxLevel = Mathf.Clamp(ResolveParam<int>(overrides, "maxHeightLevel"), 0, 6);
            float cellSize = ResolveParam<float>(overrides, "cellSize");

            _eventBus = new ScenarioEventBus();
            _service = new TerrainService(_eventBus);
            _builder = new TerrainMeshBuilder();

            _subscriptions.Add(_eventBus.Subscribe<TerrainInitializedEvent>(e =>
                Debug.Log($"[TerrainScenario] Initialized {e.Width}x{e.Depth} cell={e.CellSize}")));
            _subscriptions.Add(_eventBus.Subscribe<TerrainTileChangedEvent>(e =>
                Debug.Log($"[TerrainScenario] Tile {e.Position} -> {e.Shape} L{e.BaseLevel}")));
            _subscriptions.Add(_eventBus.Subscribe<TerrainEditRejectedEvent>(e =>
                Debug.LogWarning($"[TerrainScenario] Reject {e.Position}: {e.Reason}")));

            _service.Initialize(new TerrainBuildOptions(_width, _depth, 0, cellSize));

            int cw = _service.CornerWidth;
            int cd = _service.CornerDepth;
            var levels = new int[cw, cd];

            if (allowRamps)
            {
                var rng = new System.Random(seed);
                var candidates = new List<int>(maxLevel + 1);
                for (int z = 0; z < cd; z++)
                {
                    for (int x = 0; x < cw; x++)
                    {
                        int lo = 0, hi = maxLevel;
                        if (x > 0) { lo = Math.Max(lo, levels[x - 1, z] - 1); hi = Math.Min(hi, levels[x - 1, z] + 1); }
                        if (z > 0) { lo = Math.Max(lo, levels[x, z - 1] - 1); hi = Math.Min(hi, levels[x, z - 1] + 1); }
                        candidates.Clear();
                        for (int v = lo; v <= hi; v++)
                        {
                            if (x > 0 && z > 0)
                            {
                                var corners = new CornerHeights(
                                    NW: TerrainShapeRules.ToHeight(levels[x - 1, z]),
                                    NE: TerrainShapeRules.ToHeight(v),
                                    SE: TerrainShapeRules.ToHeight(levels[x, z - 1]),
                                    SW: TerrainShapeRules.ToHeight(levels[x - 1, z - 1]));
                                if (!TerrainShapeRules.TryClassify(corners, out _, out _)) continue;
                            }
                            candidates.Add(v);
                        }
                        if (candidates.Count == 0)
                        {
                            if (x > 0) levels[x, z] = levels[x - 1, z];
                            else if (z > 0) levels[x, z] = levels[x, z - 1];
                            else levels[x, z] = 0;
                        }
                        else
                        {
                            levels[x, z] = candidates[rng.Next(candidates.Count)];
                        }
                    }
                }
            }
            else
            {
                int uniform = new System.Random(seed).Next(0, maxLevel + 1);
                for (int z = 0; z < cd; z++)
                    for (int x = 0; x < cw; x++)
                        levels[x, z] = uniform;
            }

            var heights = new float[cw, cd];
            int flatCount = 0, rampCount = 0;
            for (int z = 0; z < cd; z++)
                for (int x = 0; x < cw; x++)
                    heights[x, z] = TerrainShapeRules.ToHeight(levels[x, z]);

            _service.TrySetAllCorners(heights);

            foreach (var pos in _service.AllTiles)
            {
                if (_service.GetTile(pos).Shape == TerrainShape.Flat) flatCount++;
                else rampCount++;
            }

            _mesh = _builder.Build(_service, TerrainPalette.BoneAndSlate);
            _meshObject = new GameObject("[Scenario] TerrainMesh");
            _meshObject.transform.SetParent(SceneRoot.transform, false);
            _meshObject.transform.localPosition = new Vector3(-_width * cellSize * 0.5f, 0f, -_depth * cellSize * 0.5f);

            var mf = _meshObject.AddComponent<MeshFilter>();
            mf.sharedMesh = _mesh;
            var mr = _meshObject.AddComponent<MeshRenderer>();
            _material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            _material.color = Color.white;
            mr.sharedMaterial = _material;

            Debug.Log($"[TerrainScenario] Generated {_width}x{_depth} terrain — {flatCount} flat, {rampCount} ramp tiles, seed={seed}");
        }

        protected override ScenarioVerificationResult VerifyInternal(ScenarioParameterOverrides overrides)
        {
            var checks = new List<ScenarioVerificationResult.CheckResult>
            {
                new("Scene root created", SceneRoot != null,
                    SceneRoot != null ? null : "SceneRoot null"),
                new("Service initialized", _service != null && _service.IsInitialized,
                    _service?.IsInitialized == true ? null : "Service not initialized"),
                new($"Dimensions {_width}x{_depth}",
                    _service != null && _service.Width == _width && _service.Depth == _depth,
                    _service != null && _service.Width == _width && _service.Depth == _depth ? null : "Dim mismatch"),
                new("Corner grid sized W+1 x D+1",
                    _service != null && _service.CornerWidth == _width + 1 && _service.CornerDepth == _depth + 1,
                    null),
                new("Mesh has vertices",
                    _mesh != null && _mesh.vertexCount > 0,
                    _mesh != null ? null : "Mesh is null"),
                new("All tiles have valid shape", AllTilesValid(),
                    AllTilesValid() ? null : "A tile has an invalid shape"),
                new("All corner heights are half-step", AllCornersHalfStep(),
                    AllCornersHalfStep() ? null : "A corner is not a multiple of 0.5u"),
            };
            return new ScenarioVerificationResult(checks);
        }

        private bool AllTilesValid()
        {
            if (_service == null) return false;
            foreach (var pos in _service.AllTiles)
            {
                var s = _service.GetTile(pos).Shape;
                if (s != TerrainShape.Flat && s != TerrainShape.RampN && s != TerrainShape.RampE
                    && s != TerrainShape.RampS && s != TerrainShape.RampW) return false;
            }
            return true;
        }

        private bool AllCornersHalfStep()
        {
            if (_service == null) return false;
            for (int z = 0; z < _service.CornerDepth; z++)
                for (int x = 0; x < _service.CornerWidth; x++)
                    if (!TerrainShapeRules.IsHalfStep(_service.GetCornerHeight(x, z))) return false;
            return true;
        }

        protected override void OnCleanup()
        {
            foreach (var sub in _subscriptions) sub.Dispose();
            _subscriptions.Clear();
            if (_mesh != null) UnityEngine.Object.DestroyImmediate(_mesh);
            if (_material != null) UnityEngine.Object.DestroyImmediate(_material);
            _eventBus?.ClearAllSubscriptions();
            _eventBus = null;
            _service = null;
            _builder = null;
            _meshObject = null;
            _mesh = null;
            _material = null;
        }
    }
}
