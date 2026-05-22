using System;
using Unity.Collections;
using Unity.Mathematics;

namespace UnityPpoRacingTrainer.Core.Track.Generation.Realistic.Native
{
    // Slices into PieceCells (footprint cells) and BoundaryPorts arrays.
    public readonly struct ShapeDescriptor
    {
        public readonly int CellStart;
        public readonly int CellCount;
        public readonly int PortStart;
        public readonly int PortCount;
        public readonly int TotalCells;
        public readonly uint KindFlags;
        public readonly FixedString64Bytes ShapeId;

        public ShapeDescriptor(
            int cellStart, int cellCount,
            int portStart, int portCount,
            int totalCells, uint kindFlags,
            FixedString64Bytes shapeId)
        {
            CellStart = cellStart;
            CellCount = cellCount;
            PortStart = portStart;
            PortCount = portCount;
            TotalCells = totalCells;
            KindFlags = kindFlags;
            ShapeId = shapeId;
        }
    }

    // KindFlags bits — fine-grained categorisation drives the heuristic's
    // shape-mix preferences (caps on short corners + u-turns; bonuses for
    // large sweeps + diagonals).
    public static class ShapeKindFlags
    {
        public const uint HasStraight = 1u << 0;
        public const uint HasCurve = 1u << 1;
        public const uint HasRamp = 1u << 2;
        public const uint HasDiagonal = 1u << 3;
        public const uint CardinalOnly = 1u << 4;

        // Category flags (one shape may have multiple).
        public const uint IsShortCorner = 1u << 5;  // RIGHT_TURN, LEFT_TURN, QUICK_RIGHT/LEFT
        public const uint IsUTurn       = 1u << 6;  // HAIRPIN, U_TURN_LONG
        public const uint IsLargeCurve  = 1u << 7;  // LONG_RIGHT, WIDE_S, S_CURVE, CHICANE, LONG_CHICANE, ZIGZAG, DETOUR
        public const uint IsDiagonalShape = 1u << 8;  // any DIAG_*
        public const uint IsPureStraight  = 1u << 9;  // LONG_STRAIGHT, SINGLE_STRAIGHT
    }

    // One footprint cell (shape-local, already rotated by piece's local facing).
    // Carries everything the terrain validator needs so we don't have to chase
    // a separate per-piece table from inside Burst.
    public readonly struct PieceCell
    {
        public readonly short Dx;
        public readonly short Dz;
        public readonly byte Family;          // TrackPieceFamily
        public readonly byte AllowedTerrain;  // TerrainShapeMask bits
        public readonly byte PieceLocalFacing; // 0..7 — for ramp world-facing solve
        public readonly byte _pad;

        public PieceCell(int dx, int dz, byte family, byte allowedTerrain, byte pieceLocalFacing)
        {
            Dx = (short)dx;
            Dz = (short)dz;
            Family = family;
            AllowedTerrain = allowedTerrain;
            PieceLocalFacing = pieceLocalFacing;
            _pad = 0;
        }
    }

    // Pre-rotated (by piece local facing) port descriptor in shape-local space.
    // Shx/Shz already include the +0.5 piece-anchor center offset, matching the
    // managed MagnetSnapResolver convention.
    public readonly struct PortDescriptor
    {
        public readonly float Shx;
        public readonly float Shz;
        public readonly byte ShapeLocalSide;   // (canonicalSide + LocalFacing) & 7
        public readonly byte PortState;
        public readonly ushort PieceIndexInShape;
        public readonly byte PortIndexInDef;
        public readonly byte _pad0;
        public readonly byte _pad1;
        public readonly byte _pad2;

        public PortDescriptor(float shx, float shz, byte shapeLocalSide, byte portState,
                              ushort pieceIndexInShape, byte portIndexInDef)
        {
            Shx = shx;
            Shz = shz;
            ShapeLocalSide = shapeLocalSide;
            PortState = portState;
            PieceIndexInShape = pieceIndexInShape;
            PortIndexInDef = portIndexInDef;
            _pad0 = 0; _pad1 = 0; _pad2 = 0;
        }
    }

    // Snapshot of the terrain category grid (Flat / CardinalRamp / AngleSlope) +
    // the cardinal ramp direction for ramp tiles (encoded in low 3 bits).
    // Layout: index = z * Width + x. Out-of-bounds reads return TerrainOOB.
    public struct TerrainSnapshot : IDisposable
    {
        public NativeArray<byte> Tiles;
        public int Width;
        public int Depth;

        public bool IsCreated => Tiles.IsCreated;

        public void Dispose()
        {
            if (Tiles.IsCreated) Tiles.Dispose();
            Width = 0;
            Depth = 0;
        }
    }

    // Encoded terrain values used by the native validator.
    // Bits: 0..3 = TerrainShapeCategory (Flat/CardinalRamp/AngleSlope/OOB)
    //       4..6 = ramp direction index (0=N,1=E,2=S,3=W) when category==CardinalRamp
    public static class TerrainEncoding
    {
        public const byte CatMask = 0x0F;
        public const byte CatFlat = 0;
        public const byte CatCardinalRamp = 1;
        public const byte CatAngleSlope = 2;
        public const byte CatOob = 3;

        public const byte RampDirShift = 4;
        public const byte RampDirMask = 0x70;

        public static byte Encode(byte category, byte rampDirIndex) =>
            (byte)((category & CatMask) | ((rampDirIndex & 0x07) << RampDirShift));

        public static byte Category(byte v) => (byte)(v & CatMask);
        public static byte RampDir(byte v) => (byte)((v & RampDirMask) >> RampDirShift);
    }

    // Per-call generator parameters bundled into a blittable struct so jobs can
    // accept it by [ReadOnly] reference without managed boxing.
    public readonly struct NativeGenerationParams
    {
        public readonly int Seed;
        public readonly int2 Origin;
        public readonly byte InitialFacing;
        public readonly int TargetLengthCells;
        public readonly int TargetLengthTolerance;
        public readonly float TurnDensity;
        public readonly int MaxConsecutiveStraights;
        public readonly float CornerSeverityBias;
        public readonly byte AllowDiagonals;
        public readonly byte RequireRamps;
        public readonly int ClosureSearchRadius;
        public readonly int BeamWidth;
        public readonly int MaxSearchSteps;

        public NativeGenerationParams(
            int seed, int2 origin, byte initialFacing,
            int targetLengthCells, int targetLengthTolerance,
            float turnDensity, int maxConsecutiveStraights,
            float cornerSeverityBias, byte allowDiagonals, byte requireRamps,
            int closureSearchRadius, int beamWidth, int maxSearchSteps)
        {
            Seed = seed;
            Origin = origin;
            InitialFacing = initialFacing;
            TargetLengthCells = targetLengthCells;
            TargetLengthTolerance = targetLengthTolerance;
            TurnDensity = turnDensity;
            MaxConsecutiveStraights = maxConsecutiveStraights;
            CornerSeverityBias = cornerSeverityBias;
            AllowDiagonals = allowDiagonals;
            RequireRamps = requireRamps;
            ClosureSearchRadius = closureSearchRadius;
            BeamWidth = beamWidth;
            MaxSearchSteps = maxSearchSteps;
        }
    }
}
