using UnityPpoRacingTrainer.Core.Terrain;
using Unidad.Core.Grid;
using UnityEngine;
using UnityEngine.InputSystem;

namespace UnityPpoRacingTrainer.Core.Track.Presentation
{
    /// <summary>
    /// Pure mouse → terrain-tile resolver. Wraps the
    /// camera + terrain-collider + terrain-service triple so scenarios don't have to
    /// re-derive the raycast pattern. A miss (no ray hit, hit a non-terrain collider,
    /// or the world point lies outside the terrain bounds) reports off-terrain.
    /// </summary>
    internal sealed class MouseInputAdapter
    {
        private readonly Camera _camera;
        private readonly GameObject _terrainObject;
        private readonly ITerrainService _terrain;
        private readonly float _maxRayDistance;

        public MouseInputAdapter(Camera camera, GameObject terrainObject, ITerrainService terrain, float maxRayDistance = 500f)
        {
            _camera = camera;
            _terrainObject = terrainObject;
            _terrain = terrain;
            _maxRayDistance = maxRayDistance;
        }

        public bool IsAvailable => _camera != null && Mouse.current != null;

        /// <summary>
        /// Returns true with <paramref name="tile"/> + <paramref name="worldHit"/> set
        /// when the mouse is over the terrain. False on any miss (raycast empty, hit
        /// non-terrain, or sample outside terrain bounds).
        /// </summary>
        public bool TryGetTileUnderMouse(out GridPosition tile, out Vector3 worldHit)
        {
            tile = default;
            worldHit = default;
            if (!IsAvailable) return false;

            var screenPos = Mouse.current.position.ReadValue();
            var ray = _camera.ScreenPointToRay(screenPos);

            if (!Physics.Raycast(ray, out var hit, _maxRayDistance)) return false;
            if (hit.collider == null || hit.collider.gameObject != _terrainObject) return false;
            if (!_terrain.TryWorldToTile(hit.point.x, hit.point.z, out var tp)) return false;

            tile = new GridPosition(tp.X, tp.Z);
            worldHit = hit.point;
            return true;
        }
    }
}
