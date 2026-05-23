// Storage note: we deliberately use plain int[,] / TerrainTile[,] instead of
// Unidad.Core.Grid.IGrid<T>. The framework's internal Grid<T> publishes
// GridCellChangedEvent through the shared IEventBus, which would leak our
// internal corner-edits to game-wide subscribers. We still reuse GridPosition
// and NeighborMode as the public coordinate vocabulary.
using System;
using System.Collections.Generic;
using Unidad.Core.EventBus;
using Unidad.Core.Grid;
using Unidad.Core.Systems;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Terrain
{
    internal sealed class TerrainService : SystemServiceBase, ITerrainService
    {
        private int[,] _cornerLevels;
        private TerrainTile[,] _tileCache;
        // Side-channel paint flag per tile. True means "render as Flat but
        // surface TerrainShape.DiagonalTile to placement / lattice classifier".
        // Storage is independent of corner heights so painting and editing
        // heights stay orthogonal.
        private bool[,] _diagonalPaint;
        private bool _initialized;
        private int _width;
        private int _depth;
        private float _cellSize;

        public TerrainService(IEventBus eventBus) : base(eventBus) { }

        public bool IsInitialized => _initialized;
        public int Width => _width;
        public int Depth => _depth;
        public float CellSize => _cellSize;
        public int CornerWidth => _initialized ? _width + 1 : 0;
        public int CornerDepth => _initialized ? _depth + 1 : 0;

        public Bounds WorldBounds
        {
            get
            {
                if (!_initialized) return new Bounds();
                float minY = float.MaxValue;
                float maxY = float.MinValue;
                for (int z = 0; z < CornerDepth; z++)
                {
                    for (int x = 0; x < CornerWidth; x++)
                    {
                        var h = TerrainShapeRules.ToHeight(_cornerLevels[x, z]);
                        if (h < minY) minY = h;
                        if (h > maxY) maxY = h;
                    }
                }
                var size = new Vector3(_width * _cellSize, Mathf.Max(maxY - minY, 0.001f), _depth * _cellSize);
                var center = new Vector3(size.x * 0.5f, (minY + maxY) * 0.5f, size.z * 0.5f);
                return new Bounds(center, size);
            }
        }

        public void Initialize(TerrainBuildOptions options)
        {
            if (options.Width <= 0 || options.Depth <= 0)
                throw new ArgumentException("Terrain dimensions must be positive.");
            if (options.CellSize <= 0f)
                throw new ArgumentException("CellSize must be positive.");

            _width = options.Width;
            _depth = options.Depth;
            _cellSize = options.CellSize;
            _cornerLevels = new int[_width + 1, _depth + 1];
            _tileCache = new TerrainTile[_width, _depth];
            _diagonalPaint = new bool[_width, _depth];

            for (int z = 0; z < _depth + 1; z++)
            {
                for (int x = 0; x < _width + 1; x++)
                {
                    _cornerLevels[x, z] = options.DefaultLevel;
                }
            }
            for (int z = 0; z < _depth; z++)
            {
                for (int x = 0; x < _width; x++)
                {
                    var pos = new TerrainPosition(x, z);
                    _tileCache[x, z] = ClassifyOrThrow(pos);
                }
            }

            _initialized = true;
            Publish(new TerrainInitializedEvent(_width, _depth, _cellSize));
        }

        public void Reset()
        {
            _cornerLevels = null;
            _tileCache = null;
            _diagonalPaint = null;
            _initialized = false;
            _width = 0;
            _depth = 0;
            _cellSize = 0f;
            Publish(new TerrainResetEvent());
        }

        public bool IsInBounds(TerrainPosition pos) =>
            _initialized && pos.X >= 0 && pos.X < _width && pos.Z >= 0 && pos.Z < _depth;

        public TerrainTile GetTile(TerrainPosition pos)
        {
            ThrowIfNotInitialized();
            ThrowIfOutOfBounds(pos);
            return _tileCache[pos.X, pos.Z];
        }

        public CornerHeights GetCorners(TerrainPosition pos)
        {
            ThrowIfNotInitialized();
            ThrowIfOutOfBounds(pos);
            return CornersOf(pos);
        }

        public IEnumerable<TerrainPosition> AllTiles
        {
            get
            {
                if (!_initialized) yield break;
                for (int z = 0; z < _depth; z++)
                    for (int x = 0; x < _width; x++)
                        yield return new TerrainPosition(x, z);
            }
        }

        public IEnumerable<TerrainPosition> GetNeighbors(TerrainPosition pos, NeighborMode mode)
        {
            if (!_initialized) yield break;
            var offsets = mode == NeighborMode.Cardinal
                ? new (int dx, int dz)[] { (0, 1), (1, 0), (0, -1), (-1, 0) }
                : new (int dx, int dz)[] { (0, 1), (1, 1), (1, 0), (1, -1), (0, -1), (-1, -1), (-1, 0), (-1, 1) };
            foreach (var (dx, dz) in offsets)
            {
                var n = new TerrainPosition(pos.X + dx, pos.Z + dz);
                if (IsInBounds(n)) yield return n;
            }
        }

        public float GetCornerHeight(int cornerX, int cornerZ)
        {
            ThrowIfNotInitialized();
            if (cornerX < 0 || cornerX >= CornerWidth || cornerZ < 0 || cornerZ >= CornerDepth)
                throw new ArgumentOutOfRangeException();
            return TerrainShapeRules.ToHeight(_cornerLevels[cornerX, cornerZ]);
        }

        public bool TrySetCornerHeight(int cornerX, int cornerZ, float height)
        {
            if (!_initialized) return false;
            if (cornerX < 0 || cornerX >= CornerWidth || cornerZ < 0 || cornerZ >= CornerDepth)
            {
                Publish(new TerrainEditRejectedEvent(default, TerrainEditRejectReason.OutOfBounds));
                return false;
            }
            if (!TerrainShapeRules.IsHalfStep(height))
            {
                Publish(new TerrainEditRejectedEvent(default, TerrainEditRejectReason.NonHalfStepHeight));
                return false;
            }

            int newLevel = TerrainShapeRules.ToLevel(height);
            int oldLevel = _cornerLevels[cornerX, cornerZ];
            if (newLevel == oldLevel) return true;

            _cornerLevels[cornerX, cornerZ] = newLevel;

            // Validate up to 4 affected tiles. Roll back on any failure.
            Span<TerrainPosition> affected = stackalloc TerrainPosition[4];
            int affectedCount = CollectAffectedTiles(cornerX, cornerZ, affected);

            for (int i = 0; i < affectedCount; i++)
            {
                if (!TerrainShapeRules.TryClassify(CornersOf(affected[i]), out _, out _))
                {
                    _cornerLevels[cornerX, cornerZ] = oldLevel;
                    Publish(new TerrainEditRejectedEvent(affected[i], TerrainEditRejectReason.InvalidCornerCombination));
                    return false;
                }
            }

            float oldHeight = TerrainShapeRules.ToHeight(oldLevel);
            float newHeight = TerrainShapeRules.ToHeight(newLevel);
            Publish(new TerrainCornerHeightChangedEvent(cornerX, cornerZ, oldHeight, newHeight));

            for (int i = 0; i < affectedCount; i++)
            {
                var pos = affected[i];
                var prior = _tileCache[pos.X, pos.Z];
                var updated = ClassifyOrThrow(pos);
                _tileCache[pos.X, pos.Z] = updated;
                if (prior.Shape != updated.Shape || prior.BaseLevel != updated.BaseLevel)
                    Publish(new TerrainTileChangedEvent(pos, updated.Shape, updated.BaseLevel));
            }

            return true;
        }

        public bool TrySetAllCorners(float[,] heights)
        {
            if (!_initialized) return false;
            int cw = CornerWidth, cd = CornerDepth;
            if (heights.GetLength(0) != cw || heights.GetLength(1) != cd)
                throw new ArgumentException(
                    $"Heights array must be [{cw}, {cd}], got [{heights.GetLength(0)}, {heights.GetLength(1)}].",
                    nameof(heights));

            for (int z = 0; z < cd; z++)
                for (int x = 0; x < cw; x++)
                    if (!TerrainShapeRules.IsHalfStep(heights[x, z]))
                    {
                        Publish(new TerrainEditRejectedEvent(default, TerrainEditRejectReason.NonHalfStepHeight));
                        return false;
                    }

            // Snapshot.
            var snapshot = (int[,])_cornerLevels.Clone();

            // Apply.
            for (int z = 0; z < cd; z++)
                for (int x = 0; x < cw; x++)
                    _cornerLevels[x, z] = TerrainShapeRules.ToLevel(heights[x, z]);

            // Validate every tile.
            foreach (var pos in AllTiles)
            {
                if (!TerrainShapeRules.TryClassify(CornersOf(pos), out _, out _))
                {
                    _cornerLevels = snapshot;
                    Publish(new TerrainEditRejectedEvent(pos, TerrainEditRejectReason.InvalidCornerCombination));
                    return false;
                }
            }

            // Recompute cache + emit per-tile events for changed tiles.
            foreach (var pos in AllTiles)
            {
                var prior = _tileCache[pos.X, pos.Z];
                var updated = ClassifyOrThrow(pos);
                _tileCache[pos.X, pos.Z] = updated;
                if (prior.Shape != updated.Shape || prior.BaseLevel != updated.BaseLevel)
                    Publish(new TerrainTileChangedEvent(pos, updated.Shape, updated.BaseLevel));
            }
            return true;
        }

        public bool TrySetTileFlat(TerrainPosition pos, int level) =>
            TrySetTileCorners(pos, TerrainShapeRules.CornersFor(TerrainShape.Flat, level));

        public bool TrySetTileRamp(TerrainPosition pos, TerrainShape ramp, int baseLevel)
        {
            if (ramp == TerrainShape.Flat) return TrySetTileFlat(pos, baseLevel);
            return TrySetTileCorners(pos, TerrainShapeRules.CornersFor(ramp, baseLevel));
        }

        private bool TrySetTileCorners(TerrainPosition pos, in CornerHeights target)
        {
            if (!_initialized || !IsInBounds(pos))
            {
                Publish(new TerrainEditRejectedEvent(pos, TerrainEditRejectReason.OutOfBounds));
                return false;
            }
            if (!TerrainShapeRules.IsHalfStep(target.NW) || !TerrainShapeRules.IsHalfStep(target.NE)
                || !TerrainShapeRules.IsHalfStep(target.SE) || !TerrainShapeRules.IsHalfStep(target.SW))
            {
                Publish(new TerrainEditRejectedEvent(pos, TerrainEditRejectReason.NonHalfStepHeight));
                return false;
            }

            int x = pos.X, z = pos.Z;
            int snapNW = _cornerLevels[x, z + 1];
            int snapNE = _cornerLevels[x + 1, z + 1];
            int snapSE = _cornerLevels[x + 1, z];
            int snapSW = _cornerLevels[x, z];

            int newNW = TerrainShapeRules.ToLevel(target.NW);
            int newNE = TerrainShapeRules.ToLevel(target.NE);
            int newSE = TerrainShapeRules.ToLevel(target.SE);
            int newSW = TerrainShapeRules.ToLevel(target.SW);

            _cornerLevels[x, z + 1] = newNW;
            _cornerLevels[x + 1, z + 1] = newNE;
            _cornerLevels[x + 1, z] = newSE;
            _cornerLevels[x, z] = newSW;

            // The 4 changed corners can affect up to 9 tiles (target + 8 neighbors).
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    var p = new TerrainPosition(x + dx, z + dz);
                    if (!IsInBounds(p)) continue;
                    if (!TerrainShapeRules.TryClassify(CornersOf(p), out _, out _))
                    {
                        _cornerLevels[x, z + 1] = snapNW;
                        _cornerLevels[x + 1, z + 1] = snapNE;
                        _cornerLevels[x + 1, z] = snapSE;
                        _cornerLevels[x, z] = snapSW;
                        Publish(new TerrainEditRejectedEvent(p, TerrainEditRejectReason.InvalidCornerCombination));
                        return false;
                    }
                }
            }

            EmitCornerChange(x, z + 1, snapNW, newNW);
            EmitCornerChange(x + 1, z + 1, snapNE, newNE);
            EmitCornerChange(x + 1, z, snapSE, newSE);
            EmitCornerChange(x, z, snapSW, newSW);

            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    var p = new TerrainPosition(x + dx, z + dz);
                    if (!IsInBounds(p)) continue;
                    var prior = _tileCache[p.X, p.Z];
                    var updated = ClassifyOrThrow(p);
                    _tileCache[p.X, p.Z] = updated;
                    if (prior.Shape != updated.Shape || prior.BaseLevel != updated.BaseLevel)
                        Publish(new TerrainTileChangedEvent(p, updated.Shape, updated.BaseLevel));
                }
            }
            return true;
        }

        private void EmitCornerChange(int cx, int cz, int oldLevel, int newLevel)
        {
            if (oldLevel == newLevel) return;
            Publish(new TerrainCornerHeightChangedEvent(cx, cz,
                TerrainShapeRules.ToHeight(oldLevel),
                TerrainShapeRules.ToHeight(newLevel)));
        }

        public bool TryWorldToTile(float worldX, float worldZ, out TerrainPosition pos)
        {
            if (!_initialized)
            {
                pos = default;
                return false;
            }
            int x = Mathf.FloorToInt(worldX / _cellSize);
            int z = Mathf.FloorToInt(worldZ / _cellSize);
            if (x == _width && Mathf.Approximately(worldX, _width * _cellSize)) x = _width - 1;
            if (z == _depth && Mathf.Approximately(worldZ, _depth * _cellSize)) z = _depth - 1;
            pos = new TerrainPosition(x, z);
            return IsInBounds(pos);
        }

        public float SampleHeight(float worldX, float worldZ)
        {
            if (!TryWorldToTile(worldX, worldZ, out var pos)) return 0f;
            var c = CornersOf(pos);
            float u = (worldX - pos.X * _cellSize) / _cellSize;
            float v = (worldZ - pos.Z * _cellSize) / _cellSize;
            u = Mathf.Clamp01(u);
            v = Mathf.Clamp01(v);
            float bottom = Mathf.Lerp(c.SW, c.SE, u);
            float top = Mathf.Lerp(c.NW, c.NE, u);
            return Mathf.Lerp(bottom, top, v);
        }

        public Vector3 SampleNormal(float worldX, float worldZ)
        {
            if (!TryWorldToTile(worldX, worldZ, out var pos)) return Vector3.up;
            var c = CornersOf(pos);
            float u = (worldX - pos.X * _cellSize) / _cellSize;
            float v = (worldZ - pos.Z * _cellSize) / _cellSize;
            u = Mathf.Clamp01(u);
            v = Mathf.Clamp01(v);
            float dydu = (1f - v) * (c.SE - c.SW) + v * (c.NE - c.NW);
            float dydv = (1f - u) * (c.NW - c.SW) + u * (c.NE - c.SE);
            var Tu = new Vector3(_cellSize, dydu, 0f);
            var Tv = new Vector3(0f, dydv, _cellSize);
            return Vector3.Cross(Tv, Tu).normalized;
        }

        // ---- internals ----

        private CornerHeights CornersOf(TerrainPosition pos)
        {
            int x = pos.X, z = pos.Z;
            return new CornerHeights(
                NW: TerrainShapeRules.ToHeight(_cornerLevels[x, z + 1]),
                NE: TerrainShapeRules.ToHeight(_cornerLevels[x + 1, z + 1]),
                SE: TerrainShapeRules.ToHeight(_cornerLevels[x + 1, z]),
                SW: TerrainShapeRules.ToHeight(_cornerLevels[x, z]));
        }

        private TerrainTile ClassifyOrThrow(TerrainPosition pos)
        {
            var corners = CornersOf(pos);
            if (!TerrainShapeRules.TryClassify(corners, out var shape, out var baseLevel))
                throw new InvalidOperationException($"Tile {pos} has invalid corner pattern: {corners}");
            // Diagonal-paint flag promotes a height-Flat tile to DiagonalTile. The
            // mesh / sample paths still see Flat corners; only placement / lattice
            // classifier observes the promotion via TerrainTile.Shape.
            if (shape == TerrainShape.Flat && _diagonalPaint != null && _diagonalPaint[pos.X, pos.Z])
                shape = TerrainShape.DiagonalTile;
            return new TerrainTile(shape, baseLevel, corners);
        }

        public bool TrySetDiagonalPaint(TerrainPosition pos, bool isDiagonal)
        {
            if (!_initialized || !IsInBounds(pos))
            {
                Publish(new TerrainEditRejectedEvent(pos, TerrainEditRejectReason.OutOfBounds));
                return false;
            }
            // Paint requires a height-flat tile. A ramp/peak/saddle paint is meaningless
            // and would silently survive a future flatten — reject up front.
            var prior = _tileCache[pos.X, pos.Z];
            bool currentlyFlat = prior.Shape == TerrainShape.Flat || prior.Shape == TerrainShape.DiagonalTile;
            if (isDiagonal && !currentlyFlat)
            {
                Publish(new TerrainEditRejectedEvent(pos, TerrainEditRejectReason.InvalidCornerCombination));
                return false;
            }
            if (_diagonalPaint[pos.X, pos.Z] == isDiagonal) return true;
            _diagonalPaint[pos.X, pos.Z] = isDiagonal;
            var updated = ClassifyOrThrow(pos);
            _tileCache[pos.X, pos.Z] = updated;
            if (prior.Shape != updated.Shape)
                Publish(new TerrainTileChangedEvent(pos, updated.Shape, updated.BaseLevel));
            return true;
        }

        public bool GetDiagonalPaint(TerrainPosition pos)
        {
            if (!_initialized || !IsInBounds(pos)) return false;
            return _diagonalPaint[pos.X, pos.Z];
        }

        private int CollectAffectedTiles(int cornerX, int cornerZ, Span<TerrainPosition> buffer)
        {
            int count = 0;
            for (int dz = -1; dz <= 0; dz++)
            {
                for (int dx = -1; dx <= 0; dx++)
                {
                    var p = new TerrainPosition(cornerX + dx, cornerZ + dz);
                    if (IsInBounds(p)) buffer[count++] = p;
                }
            }
            return count;
        }

        private void ThrowIfNotInitialized()
        {
            if (!_initialized) throw new InvalidOperationException("Terrain is not initialized.");
        }

        private void ThrowIfOutOfBounds(TerrainPosition pos)
        {
            if (!IsInBounds(pos))
                throw new ArgumentOutOfRangeException(nameof(pos),
                    $"Position {pos} out of bounds ({_width}x{_depth}).");
        }
    }
}
