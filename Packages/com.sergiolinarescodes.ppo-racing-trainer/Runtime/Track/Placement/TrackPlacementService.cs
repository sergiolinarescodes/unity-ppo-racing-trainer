using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.Terrain;
using Unidad.Core.EventBus;
using Unidad.Core.Factory;
using Unidad.Core.Grid;
using Unidad.Core.Systems;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Track
{
    /// <summary>
    /// Orchestrates placement: validator pipeline → mesh build → GameObject spawn →
    /// event publish. Owns the occupancy map (tile → piece id) the overlap validator
    /// reads from. Materials are created once per service and reused.
    /// Also forwards each spawned piece's wall geometry to
    /// <see cref="ITrackCollisionService"/>, transforming canonical-local coords
    /// to world space using the same pose the GameObject receives. Static kerbs were
    /// removed — kerbs are now placed dynamically by the racing-line kerb service
    /// during the ghost-loop preview.
    /// </summary>
    internal sealed class TrackPlacementService : SystemServiceBase, ITrackPlacementService
    {
        // v8j: global gate for wall registration. TrainerBootstrap flips false on
        // stage 0 (Warmup) so the policy can learn baseline driving on a wall-free
        // track. Default true preserves authored / play-mode geometry.
        public static bool EmitWalls = true;

        private static readonly string ShaderName = "RaceConstructor/TerrainVertexColor";

        // Resources path for the modular F1-style wall barrier prefab.
        private const string WallBarrierResourcePath = "Track/WallBarrier";

        private readonly ITrackPieceCatalog _catalog;
        private readonly ITrackPieceMeshBuilder _meshBuilder;
        private readonly IReadOnlyList<ITrackPlacementValidator> _validators;
        private readonly ITerrainService _terrain;
        private readonly IGameObjectFactory _factory;
        private readonly TrackPalette _palette;
        private readonly ITrackCollisionService _collision;
        private readonly ITrackPiecePlacementAnimator _placementAnimator;

        private readonly Dictionary<TrackPieceId, TrackPiece> _pieces = new();
        private readonly Dictionary<GridPosition, TrackPieceId> _occupancy = new();
        private readonly Dictionary<TrackPieceId, GameObject> _gameObjects = new();
        private Material _sharedMaterial;

        public TrackPlacementService(
            IEventBus eventBus,
            ITrackPieceCatalog catalog,
            ITrackPieceMeshBuilder meshBuilder,
            IReadOnlyList<ITrackPlacementValidator> validators,
            ITerrainService terrain,
            IGameObjectFactory factory,
            TrackPalette palette,
            ITrackCollisionService collision = null,
            ITrackPiecePlacementAnimator placementAnimator = null) : base(eventBus)
        {
            _catalog = catalog;
            _meshBuilder = meshBuilder;
            _validators = validators;
            _terrain = terrain;
            _factory = factory;
            _palette = palette;
            _collision = collision;
            _placementAnimator = placementAnimator;
        }

        public IReadOnlyCollection<TrackPiece> Placed => _pieces.Values;
        public IReadOnlyDictionary<GridPosition, TrackPieceId> Occupancy => _occupancy;

        public PlacementResult TryPlace(TrackPieceShape shape, GridPosition origin, TrackDirection facing)
            => TryPlace(shape, origin, facing, TrackPieceVariantId.Default, animate: true);

        public PlacementResult TryPlace(TrackPieceShape shape, GridPosition origin, TrackDirection facing, TrackPieceVariantId variantId)
            => TryPlace(shape, origin, facing, variantId, animate: true);

        public PlacementResult TryPlace(TrackPieceShape shape, GridPosition origin, TrackDirection facing, TrackPieceVariantId variantId, bool animate)
        {
            if (!_catalog.TryGet(shape, out var def))
            {
                var reason = $"Unknown shape '{shape}'";
                Publish(new TrackPiecePlacementRejectedEvent(shape, origin, facing, reason));
                return PlacementResult.Rejected(reason);
            }

            var ctx = new PlacementContext(def, origin, facing, _terrain, _occupancy);
            foreach (var validator in _validators)
            {
                var result = validator.Validate(ctx);
                if (!result.IsValid)
                {
                    Publish(new TrackPiecePlacementRejectedEvent(shape, origin, facing, result.Reason));
                    return PlacementResult.Rejected(result.Reason);
                }
            }

            var id = TrackPieceId.New();
            var piece = new TrackPiece(id, shape, origin, facing, variantId);
            _pieces[id] = piece;
            foreach (var cell in def.Footprint.Tiles(origin, facing))
                _occupancy[cell] = id;

            var go = SpawnGameObject(def, origin, facing, id, variantId);
            _gameObjects[id] = go;

            if (animate && _placementAnimator != null)
                _placementAnimator.StartDrop(id, go);

            Publish(new TrackPiecePlacedEvent(id, shape, origin, facing));
            return PlacementResult.Ok(id);
        }

        public bool Remove(TrackPieceId id)
        {
            if (!_pieces.TryGetValue(id, out var piece)) return false;
            if (!_catalog.TryGet(piece.Shape, out var def)) return false;

            foreach (var cell in def.Footprint.Tiles(piece.Origin, piece.Facing))
                _occupancy.Remove(cell);
            _pieces.Remove(id);
            if (_gameObjects.TryGetValue(id, out var go))
            {
                _factory.Destroy(go);
                _gameObjects.Remove(id);
            }
            _collision?.Unregister(id);
            Publish(new TrackPieceRemovedEvent(id));
            return true;
        }

        public GameObject GetGameObject(TrackPieceId id)
            => _gameObjects.TryGetValue(id, out var go) ? go : null;

        public bool TryGetPiece(TrackPieceId id, out TrackPiece piece)
            => _pieces.TryGetValue(id, out piece);

        public void Clear()
        {
            var ids = new List<TrackPieceId>(_pieces.Keys);
            foreach (var id in ids) Remove(id);
            _collision?.Clear();
        }

        private GameObject SpawnGameObject(TrackPieceDefinition def, GridPosition origin, TrackDirection facing, TrackPieceId id, TrackPieceVariantId variantId)
        {
            var build = _meshBuilder.Build(def, _palette, variantId);

            var go = _factory.CreateEmpty($"TrackPiece_{def.Shape.Id}_{id}");
            var (pos, rot, scale) = TrackPiecePoseResolver.Resolve(_terrain, def, origin, facing);
            go.transform.position = pos;
            go.transform.rotation = rot;
            go.transform.localScale = Vector3.one * scale;

            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = build.Mesh;

            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = ResolveSharedMaterial();

            RegisterCollision(id, build, pos, rot, scale);
            SpawnWallBarriers(go, build);
            return go;
        }

        /// <summary>
        /// Instantiates <c>Track/WallBarrier</c> prefabs along every wall edge.
        /// One prefab per <see cref="WallBarrierPlacement"/> emitted by the mesh
        /// strategies — straights produce ceil(L) tiled barriers, curves produce
        /// one barrier per arc tessellation chord. Prefabs are parented to the
        /// piece GO; localScale.x stretches the model to match the slot length so
        /// curve chords stay seamless.
        /// </summary>
        private void SpawnWallBarriers(GameObject parent, MeshBuildResult build)
        {
            if (build.WallBarriers == null || build.WallBarriers.Count == 0) return;

            for (int i = 0; i < build.WallBarriers.Count; i++)
            {
                var b = build.WallBarriers[i];
                var inst = _factory.InstantiatePrefab(WallBarrierResourcePath, $"Wall_{i}", Vector3.zero);
                if (inst == null) continue;

                inst.transform.SetParent(parent.transform, worldPositionStays: false);
                inst.transform.localPosition = new Vector3(b.CenterXZ.x, 0f, b.CenterXZ.y);
                inst.transform.localRotation = WallBarrierRotation(b.ForwardXZ);
                // Prefab is authored 1.0u long along its local +X. Stretch X to slot
                // length so curve chords (slightly < 1u) tile without gaps; Y / Z use
                // the prefab's authored size unchanged.
                inst.transform.localScale = new Vector3(b.Length, 1f, 1f);
            }
        }

        /// <summary>
        /// Rotation that aligns the prefab's local +X axis with the wall tangent
        /// (<paramref name="forwardXZ"/>). LookRotation puts +Z on the tangent, so
        /// the prefab's +X lands as the cross-track perpendicular — barrier model is
        /// authored with +X along the wall, so this works after the -90° pre-rotation.
        /// </summary>
        private static Quaternion WallBarrierRotation(Vector2 forwardXZ)
        {
            if (forwardXZ.sqrMagnitude < 1e-8f) return Quaternion.identity;
            Vector3 fwd = new(forwardXZ.x, 0f, forwardXZ.y);
            return Quaternion.LookRotation(fwd, Vector3.up) * Quaternion.Euler(0f, -90f, 0f);
        }

        /// <summary>
        /// Transforms the canonical-local wall segments through the same pose the
        /// GameObject receives, stamps the new owner id, and hands them to the
        /// collision service. Passes an empty kerb list — static kerbs were removed;
        /// the racing-line kerb service registers dynamic kerbs separately during
        /// the ghost-loop preview. Skipped silently when no collision service is wired
        /// (e.g. headless tests that don't care about wall hits).
        /// </summary>
        private void RegisterCollision(TrackPieceId id, MeshBuildResult build, Vector3 pos, Quaternion rot, float scale)
        {
            if (_collision == null) return;
            if (build.Walls == null || build.Walls.Count == 0) return;

            var walls = new List<WallSegment>(build.Walls.Count);
            // v8j: gate wall registration via static EmitWalls flag. TrainerBootstrap
            // sets this false on stage 0 (Warmup) so the policy can learn baseline
            // movement on a wall-free track before walls are introduced. Default true
            // preserves authored-circuit + non-trainer behavior.
            if (EmitWalls)
            {
                for (int i = 0; i < build.Walls.Count; i++)
                {
                    var w = build.Walls[i];
                    Vector2 wa = TransformXZ(w.A, pos, rot, scale);
                    Vector2 wb = TransformXZ(w.B, pos, rot, scale);
                    walls.Add(new WallSegment(wa, wb, w.Height * scale, pos.y + w.BaseY * scale, id));
                }
            }

            _collision.Register(id, walls, System.Array.Empty<IKerbZone>());
        }

        private static Vector2 TransformXZ(Vector2 localXZ, Vector3 pos, Quaternion rot, float scale)
        {
            Vector3 local = new(localXZ.x * scale, 0f, localXZ.y * scale);
            Vector3 world = pos + rot * local;
            return new Vector2(world.x, world.z);
        }

        private Material ResolveSharedMaterial()
        {
            if (_sharedMaterial != null) return _sharedMaterial;
            // Player builds strip shaders that aren't referenced by serialized scene
            // assets, so Shader.Find can return null in headless / training builds.
            // Fall back through Standard then the always-present InternalErrorShader
            // so bootstrap never throws on Material(null).
            var shader = Shader.Find(ShaderName)
                         ?? Shader.Find("Universal Render Pipeline/Lit")
                         ?? Shader.Find("Standard")
                         ?? Shader.Find("Hidden/InternalErrorShader");
            if (shader == null)
            {
                Debug.LogWarning("[TrackPlacementService] no shader available — track pieces will render with default material.");
                _sharedMaterial = new Material(Shader.Find("Hidden/InternalErrorShader"));
            }
            else
            {
                _sharedMaterial = new Material(shader) { name = "TrackPiece_SharedMaterial" };
            }
            return _sharedMaterial;
        }

        public override void Dispose()
        {
            Clear();
            if (_sharedMaterial != null)
            {
                if (Application.isPlaying) Object.Destroy(_sharedMaterial);
                else Object.DestroyImmediate(_sharedMaterial);
                _sharedMaterial = null;
            }
            base.Dispose();
        }
    }
}
