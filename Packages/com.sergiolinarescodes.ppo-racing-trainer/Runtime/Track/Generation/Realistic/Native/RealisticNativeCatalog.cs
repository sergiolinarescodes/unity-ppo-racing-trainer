using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.Terrain;
using UnityPpoRacingTrainer.Core.Track.Geometry;
using UnityPpoRacingTrainer.Core.Track.Shape;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Track.Generation.Realistic.Native
{
    // Flat blittable views into the player's managed shape + piece catalogs. Built
    // once at boot from ITrackShapeCatalog + ITrackPieceCatalog and reused across
    // every generator attempt. Disposed at app shutdown.
    public sealed class RealisticNativeCatalog : IDisposable
    {
        private readonly ITrackShapeCatalog _shapeCatalog;
        private readonly ITrackPieceCatalog _pieceCatalog;

        private NativeArray<ShapeDescriptor> _shapes;
        private NativeArray<PieceCell> _cells;
        private NativeArray<PortDescriptor> _ports;
        private List<TrackShapeId> _shapeIds;
        private bool _baked;

        public RealisticNativeCatalog(ITrackShapeCatalog shapeCatalog, ITrackPieceCatalog pieceCatalog)
        {
            _shapeCatalog = shapeCatalog;
            _pieceCatalog = pieceCatalog;
        }

        public bool IsBaked => _baked;
        public int ShapeCount => _baked ? _shapes.Length : 0;
        public NativeArray<ShapeDescriptor> Shapes
        {
            get { EnsureBaked(); return _shapes; }
        }
        public NativeArray<PieceCell> Cells
        {
            get { EnsureBaked(); return _cells; }
        }
        public NativeArray<PortDescriptor> Ports
        {
            get { EnsureBaked(); return _ports; }
        }
        public IReadOnlyList<TrackShapeId> ShapeIds
        {
            get { EnsureBaked(); return _shapeIds; }
        }

        public void EnsureBaked()
        {
            if (_baked) return;
            Bake();
            _baked = true;
        }

        public void Rebake()
        {
            Dispose();
            _baked = false;
            EnsureBaked();
        }

        private void Bake()
        {
            var allShapes = _shapeCatalog.All;
            var cellList = new List<PieceCell>(allShapes.Count * 6);
            var portList = new List<PortDescriptor>(allShapes.Count * 4);
            var shapeList = new ShapeDescriptor[allShapes.Count];
            _shapeIds = new List<TrackShapeId>(allShapes.Count);

            for (int s = 0; s < allShapes.Count; s++)
            {
                var shape = allShapes[s];
                int cellStart = cellList.Count;
                int portStart = portList.Count;
                uint kindFlags = 0;
                int totalCells = 0;

                bool anyDiagonal = false;
                bool anyCardinalOnly = true;

                for (int i = 0; i < shape.Pieces.Count; i++)
                {
                    var sp = shape.Pieces[i];
                    if (!_pieceCatalog.TryGet(sp.PieceType, out var def)) continue;

                    byte family = (byte)def.Family;
                    byte allowedTerrain = (byte)def.AllowedTerrain;
                    byte pieceLocal = (byte)sp.LocalFacing;

                    if (def.Family == TrackPieceFamily.Straight) kindFlags |= ShapeKindFlags.HasStraight;
                    else if (def.Family == TrackPieceFamily.Curve) kindFlags |= ShapeKindFlags.HasCurve;
                    else if (def.Family == TrackPieceFamily.Ramp) kindFlags |= ShapeKindFlags.HasRamp;
                    if (def.Family == TrackPieceFamily.DiagonalStraight ||
                        def.Family == TrackPieceFamily.DiagonalCurve)
                    {
                        kindFlags |= ShapeKindFlags.HasDiagonal;
                        anyDiagonal = true;
                    }

                    if (!sp.LocalFacing.IsCardinal()) anyCardinalOnly = false;

                    int W = def.Dimensions.Width;
                    int L = def.Dimensions.Length;
                    for (int lz = 0; lz < L; lz++)
                    {
                        for (int lx = 0; lx < W; lx++)
                        {
                            var (dxRel, dzRel) = TrackPieceFootprint.RotateOffset(lx, lz, sp.LocalFacing);
                            cellList.Add(new PieceCell(
                                sp.Offset.Dx + dxRel,
                                sp.Offset.Dz + dzRel,
                                family,
                                allowedTerrain,
                                pieceLocal));
                            totalCells++;
                        }
                    }
                }

                // Boundary ports — same algorithm as ShapeBoundaryPorts.Enumerate.
                var boundary = ShapeBoundaryPorts.Enumerate(shape, _pieceCatalog);
                for (int b = 0; b < boundary.Count; b++)
                {
                    var entry = boundary[b];
                    if (entry.PieceIndex < 0 || entry.PieceIndex >= shape.Pieces.Count) continue;
                    var sp = shape.Pieces[entry.PieceIndex];
                    if (!_pieceCatalog.TryGet(sp.PieceType, out var def)) continue;
                    if (entry.PortIndex < 0 || entry.PortIndex >= def.Ports.Count) continue;

                    var port = def.Ports[entry.PortIndex];
                    Vector2 local = PortGeometry.CanonicalLocal(def, port);
                    float lx = PortGeometry.MirrorXLocal(local.x, def.MirrorX);
                    float lz = local.y;
                    var rot = PortGeometry.RotateAroundAnchor(lx, lz, sp.LocalFacing);
                    float shx = sp.Offset.Dx + 0.5f + rot.x;
                    float shz = sp.Offset.Dz + 0.5f + rot.y;
                    byte shapeLocalSide = (byte)(((byte)port.Side + (byte)sp.LocalFacing) & 7);

                    portList.Add(new PortDescriptor(
                        shx, shz, shapeLocalSide, (byte)port.State,
                        (ushort)entry.PieceIndex, (byte)entry.PortIndex));
                }

                if (anyCardinalOnly && !anyDiagonal) kindFlags |= ShapeKindFlags.CardinalOnly;

                // Category tagging by shape id — drives heuristic preferences.
                var idStr2 = shape.Id.Id ?? string.Empty;
                if (idStr2 == "RIGHT_TURN" || idStr2 == "LEFT_TURN"
                    || idStr2 == "QUICK_RIGHT" || idStr2 == "QUICK_LEFT")
                    kindFlags |= ShapeKindFlags.IsShortCorner;
                else if (idStr2 == "HAIRPIN" || idStr2 == "U_TURN_LONG")
                    kindFlags |= ShapeKindFlags.IsUTurn;
                else if (idStr2 == "LONG_RIGHT" || idStr2 == "WIDE_S"
                    || idStr2 == "S_CURVE" || idStr2 == "CHICANE"
                    || idStr2 == "LONG_CHICANE" || idStr2 == "DETOUR"
                    || idStr2 == "LOOP_QUARTER")
                    kindFlags |= ShapeKindFlags.IsLargeCurve;
                // ZIGZAG / Z_STEP_RIGHT / Z_STEP_LEFT intentionally NOT tagged
                // as large curves — they're tight wiggles. They still get the
                // generic per-curve bonus, just not the "flowing sweep" boost.
                if (idStr2.StartsWith("DIAG_"))
                    kindFlags |= ShapeKindFlags.IsDiagonalShape;
                if (idStr2 == "LONG_STRAIGHT" || idStr2 == "SINGLE_STRAIGHT")
                    kindFlags |= ShapeKindFlags.IsPureStraight;

                var idStr = shape.Id.Id ?? string.Empty;
                FixedString64Bytes idBytes = default;
                idBytes.CopyFromTruncated(idStr);

                shapeList[s] = new ShapeDescriptor(
                    cellStart, cellList.Count - cellStart,
                    portStart, portList.Count - portStart,
                    totalCells, kindFlags, idBytes);

                _shapeIds.Add(shape.Id);
            }

            _shapes = new NativeArray<ShapeDescriptor>(shapeList, Allocator.Persistent);
            _cells = new NativeArray<PieceCell>(cellList.Count, Allocator.Persistent);
            for (int i = 0; i < cellList.Count; i++) _cells[i] = cellList[i];
            _ports = new NativeArray<PortDescriptor>(portList.Count, Allocator.Persistent);
            for (int i = 0; i < portList.Count; i++) _ports[i] = portList[i];
        }

        public void Dispose()
        {
            if (_shapes.IsCreated) _shapes.Dispose();
            if (_cells.IsCreated) _cells.Dispose();
            if (_ports.IsCreated) _ports.Dispose();
            _shapeIds = null;
            _baked = false;
        }

        // Snapshot the terrain category grid for native consumption. Caller owns
        // the returned snapshot's NativeArray and must Dispose it when the
        // generator attempt finishes.
        public static TerrainSnapshot SnapshotTerrain(ITerrainService terrain, Allocator allocator)
        {
            if (terrain == null || !terrain.IsInitialized)
            {
                return new TerrainSnapshot { Tiles = default, Width = 0, Depth = 0 };
            }
            int w = terrain.Width;
            int d = terrain.Depth;
            var arr = new NativeArray<byte>(w * d, allocator, NativeArrayOptions.UninitializedMemory);
            for (int z = 0; z < d; z++)
            {
                for (int x = 0; x < w; x++)
                {
                    var pos = new TerrainPosition(x, z);
                    if (!terrain.IsInBounds(pos))
                    {
                        arr[z * w + x] = TerrainEncoding.Encode(TerrainEncoding.CatOob, 0);
                        continue;
                    }
                    var ts = terrain.GetTile(pos).Shape;
                    var cat = ts.GetCategory();
                    byte cb = cat switch
                    {
                        TerrainShapeCategory.Flat => TerrainEncoding.CatFlat,
                        TerrainShapeCategory.CardinalRamp => TerrainEncoding.CatCardinalRamp,
                        TerrainShapeCategory.AngleSlope => TerrainEncoding.CatAngleSlope,
                        _ => TerrainEncoding.CatOob,
                    };
                    byte rampDir = ts switch
                    {
                        TerrainShape.RampN => 0,
                        TerrainShape.RampE => 1,
                        TerrainShape.RampS => 2,
                        TerrainShape.RampW => 3,
                        _ => 0,
                    };
                    arr[z * w + x] = TerrainEncoding.Encode(cb, rampDir);
                }
            }
            return new TerrainSnapshot { Tiles = arr, Width = w, Depth = d };
        }
    }

}
