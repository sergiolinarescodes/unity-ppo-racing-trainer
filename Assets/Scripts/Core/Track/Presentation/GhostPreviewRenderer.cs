using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.Terrain;
using UnityPpoRacingTrainer.Core.Track.Shape;
using Unidad.Core.Factory;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Track.Presentation
{
    /// <summary>
    /// Owns the per-piece ghost mesh pool, transparent material lifecycle, and tinting.
    /// Render(preview) acquires/parks ghost instances each call so the active set
    /// matches the latest preview exactly; Dispose tears down the materials.
    ///
    /// Pulled out of the placement scenario so any scenario or game-mode UI that
    /// previews shape placement can reuse the same renderer.
    /// </summary>
    internal sealed class GhostPreviewRenderer
    {
        private readonly ITrackPieceCatalog _catalog;
        private readonly ITrackPieceMeshBuilder _meshBuilder;
        private readonly IGameObjectFactory _factory;
        private readonly GameObject _root;
        private readonly Color _validColor;
        private readonly Color _invalidColor;
        private readonly Color _magnetColor;

        // Pool key includes variant id so a ghost preview rendered with V-key cycling
        // pulls the right per-variant mesh. Default-variant placements share the
        // same key (TrackPieceVariantId.Default) so legacy single-variant pieces
        // keep their existing pool slot.
        private readonly Dictionary<(TrackPieceShape, TrackPieceVariantId), Queue<GhostInstance>> _free = new();
        private readonly List<GhostInstance> _active = new();
        private readonly List<GhostInstance> _all = new();

        public GhostPreviewRenderer(
            ITrackPieceCatalog catalog,
            ITrackPieceMeshBuilder meshBuilder,
            IGameObjectFactory factory,
            GameObject root,
            Color validColor,
            Color invalidColor,
            Color magnetColor)
        {
            _catalog = catalog;
            _meshBuilder = meshBuilder;
            _factory = factory;
            _root = root;
            _validColor = validColor;
            _invalidColor = invalidColor;
            _magnetColor = magnetColor;
            // Variant pools are created lazily on first Acquire so we don't pre-allocate
            // for variants that may never get cycled in this scenario.
        }

        public void Render(ShapePreviewResult preview, ITerrainService terrain, float deltaTime, bool magnetActive, bool forceSnap = false)
            => Render(preview, terrain, deltaTime, magnetActive, TrackPieceVariantId.Default, forceSnap);

        public void Render(ShapePreviewResult preview, ITerrainService terrain, float deltaTime, bool magnetActive, TrackPieceVariantId variantId, bool forceSnap = false)
        {
            HideAll();
            // Always snap — the ghost pool is keyed by piece-type, so for shapes with
            // repeated pieces (Chicane = 3×Straight + 2×Curve, U-Turn-Long, Z-steps,
            // Loop-Quarter, Long-Right) the queue-order across frames doesn't match
            // piece-slot order, and any frame-to-frame lerp would animate ghosts
            // between unrelated slots — produced visibly compressed / mis-shaped
            // green previews. Snapping every frame trades smoothness for correctness.

            for (int i = 0; i < preview.Pieces.Count; i++)
            {
                var p = preview.Pieces[i];
                var ghost = Acquire(p.PieceType, variantId);
                if (ghost == null) continue;
                if (!_catalog.TryGet(p.PieceType, out var def)) continue;

                var (pos, rot, scale) = TrackPiecePoseResolver.Resolve(terrain, def, p.Tile, p.ResolvedFacing);
                ghost.Root.transform.localScale = Vector3.one * scale;
                ghost.Root.transform.SetPositionAndRotation(pos, rot);
                ghost.Initialized = true;

                ghost.Material.color = !p.Valid ? _invalidColor : (magnetActive ? _magnetColor : _validColor);
                ghost.Root.SetActive(true);
                _active.Add(ghost);
            }
        }

        public void HideAll()
        {
            for (int i = 0; i < _active.Count; i++)
            {
                var g = _active[i];
                g.Root.SetActive(false);
                _free[(g.PieceType, g.VariantId)].Enqueue(g);
            }
            _active.Clear();
        }

        public void Dispose()
        {
            for (int i = 0; i < _all.Count; i++)
                if (_all[i].Material != null) Object.DestroyImmediate(_all[i].Material);
            _all.Clear();
            _active.Clear();
            _free.Clear();
        }

        private GhostInstance Acquire(TrackPieceShape pieceType, TrackPieceVariantId variantId)
        {
            var key = (pieceType, variantId);
            if (!_free.TryGetValue(key, out var queue))
            {
                queue = new Queue<GhostInstance>();
                _free[key] = queue;
            }
            if (queue.Count > 0) return queue.Dequeue();
            return Create(pieceType, variantId);
        }

        private GhostInstance Create(TrackPieceShape pieceType, TrackPieceVariantId variantId)
        {
            if (!_catalog.TryGet(pieceType, out var def)) return null;

            var mesh = _meshBuilder.Build(def, TrackPalette.Default, variantId).Mesh;
            var nameSuffix = variantId.Index == 0 ? string.Empty : $"_v{variantId.Index}";
            var go = _factory.CreateEmpty($"Ghost_{pieceType.Id}{nameSuffix}");
            go.transform.SetParent(_root.transform, false);
            go.SetActive(false);

            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;

            var mr = go.AddComponent<MeshRenderer>();
            var mat = CreateTransparentMaterial($"Ghost_{pieceType.Id}{nameSuffix}_Mat");
            mr.sharedMaterial = mat;

            var ghost = new GhostInstance(pieceType, variantId, go, mat);
            _all.Add(ghost);
            return ghost;
        }

        private static Material CreateTransparentMaterial(string name)
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit")
                         ?? Shader.Find("Unlit/Color")
                         ?? Shader.Find("Universal Render Pipeline/Lit");
            var mat = new Material(shader) { name = name };
            // URP/Unlit transparency: _Surface=Transparent, _BaseColor with alpha. Built-in falls back to _Color.
            if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 1f);
            if (mat.HasProperty("_Blend")) mat.SetFloat("_Blend", 0f);
            if (mat.HasProperty("_SrcBlend")) mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            if (mat.HasProperty("_DstBlend")) mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            if (mat.HasProperty("_ZWrite")) mat.SetFloat("_ZWrite", 0f);
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            return mat;
        }

        private sealed class GhostInstance
        {
            public readonly TrackPieceShape PieceType;
            public readonly TrackPieceVariantId VariantId;
            public readonly GameObject Root;
            public readonly Material Material;
            public bool Initialized;

            public GhostInstance(TrackPieceShape pieceType, TrackPieceVariantId variantId, GameObject root, Material material)
            {
                PieceType = pieceType;
                VariantId = variantId;
                Root = root;
                Material = material;
            }
        }
    }
}
