using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.Terrain;
using Unidad.Core.EventBus;
using Unidad.Core.Factory;
using Unidad.Core.Systems;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Track.Ribbon
{
    internal sealed class TrackRibbonService : SystemServiceBase, ITrackRibbonService
    {
        private static readonly string ShaderName = "RaceConstructor/TerrainVertexColor";

        private readonly ITrackPlacementService _placement;
        private readonly ITerrainService _terrain;
        private readonly TrackChainExtractor _extractor;
        private readonly TrackRibbonMeshBuilder _meshBuilder;
        private readonly IGameObjectFactory _factory;

        private readonly List<GameObject> _ribbons = new();
        private readonly List<Mesh> _meshes = new();
        private Material _sharedMaterial;
        // Headless / shader-stripped builds (PPO trainer .exe) can't resolve the
        // ribbon shader — training doesn't render anything, so disable the service
        // after the first miss instead of throwing on every TrackPiecePlacedEvent.
        private bool _shaderMissing;

        public TrackRibbonService(
            IEventBus eventBus,
            ITrackPlacementService placement,
            ITrackPieceCatalog catalog,
            ITerrainService terrain,
            IGameObjectFactory factory,
            TrackPalette palette) : base(eventBus)
        {
            _placement = placement;
            _terrain = terrain;
            _extractor = new TrackChainExtractor(catalog);
            _meshBuilder = new TrackRibbonMeshBuilder(terrain, palette);
            _factory = factory;

            Subscribe<TrackPiecePlacedEvent>(_ => Rebuild());
            Subscribe<TrackPieceRemovedEvent>(_ => Rebuild());
        }

        public IReadOnlyList<GameObject> Ribbons => _ribbons;

        public void Rebuild()
        {
            if (_shaderMissing) return;
            ClearOutput();
            var material = ResolveMaterial();
            if (material == null) return;
            float cellSize = _terrain != null && _terrain.IsInitialized ? _terrain.CellSize : 1f;
            var chains = _extractor.Extract(_placement.Placed, cellSize);
            for (int i = 0; i < chains.Count; i++)
            {
                var mesh = _meshBuilder.Build(chains[i], $"TrackRibbon_{i}");
                if (mesh == null) continue;
                _meshes.Add(mesh);

                var go = _factory.CreateEmpty($"TrackRibbon_{i}");
                go.transform.position = Vector3.zero;
                go.transform.rotation = Quaternion.identity;
                var mf = go.AddComponent<MeshFilter>();
                mf.sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = material;
                _ribbons.Add(go);
            }
        }

        private Material ResolveMaterial()
        {
            if (_sharedMaterial != null) return _sharedMaterial;
            var shader = Shader.Find(ShaderName) ?? Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                _shaderMissing = true;
                Debug.LogWarning($"[TrackRibbonService] Neither '{ShaderName}' nor URP/Lit available — disabling ribbon rendering (headless / stripped build).");
                return null;
            }
            _sharedMaterial = new Material(shader) { name = "TrackRibbon_SharedMaterial" };
            return _sharedMaterial;
        }

        private void ClearOutput()
        {
            foreach (var go in _ribbons)
                if (go != null) _factory.Destroy(go);
            _ribbons.Clear();

            foreach (var m in _meshes)
                DestroyAny(m);
            _meshes.Clear();
        }

        public override void Dispose()
        {
            ClearOutput();
            if (_sharedMaterial != null)
            {
                DestroyAny(_sharedMaterial);
                _sharedMaterial = null;
            }
            base.Dispose();
        }

        // Picks Destroy (Play) vs. DestroyImmediate (Edit) — scenarios run in
        // edit mode where Object.Destroy is deferred and leaks Mesh/Material assets.
        private static void DestroyAny(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Object.Destroy(o);
            else Object.DestroyImmediate(o);
        }
    }
}
