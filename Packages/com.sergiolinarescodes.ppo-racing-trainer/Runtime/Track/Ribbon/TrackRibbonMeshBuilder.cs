using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.Terrain;
using UnityPpoRacingTrainer.Core.Track.Ribbon.Filters;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Track.Ribbon
{
    /// <summary>
    /// Builds one ribbon Mesh per chain. Three things drive the look:
    ///
    /// 1. Per-sample terrain Y is sampled along the chain centerline, then run
    ///    through a <see cref="IRibbonYProfileFilter"/> chain (default: moving
    ///    average + min-clamp lift). Bilinear terrain has slope discontinuities
    ///    at tile boundaries — without smoothing the road shows a hard angle
    ///    exactly where two tile shapes meet. The lift step prevents smoothing
    ///    from pulling the road below the per-piece slab tops at peaks.
    ///
    /// 2. Apron outer Y stays on RAW terrain so the skirt always meets the
    ///    ground exactly. The kink stays on the apron's outermost edge (where
    ///    it's at a glancing angle and hard to see) instead of on the road.
    ///
    /// 3. The cross-section spans road-edge → fillet → apron-outer with N
    ///    subdivisions per side. Y across the fillet is blended via a
    ///    smoothstep so the road transitions to terrain along an S-curve,
    ///    not a hard ramp.
    /// </summary>
    public sealed class TrackRibbonMeshBuilder
    {
        private readonly ITerrainService _terrain;
        private readonly TrackPalette _palette;
        private readonly IReadOnlyList<IRibbonYProfileFilter> _yFilters;
        private readonly MeshBuffer _buffer = new();

        public TrackRibbonMeshBuilder(ITerrainService terrain, TrackPalette palette)
            : this(terrain, palette, DefaultYFilters()) { }

        public TrackRibbonMeshBuilder(
            ITerrainService terrain, TrackPalette palette,
            IReadOnlyList<IRibbonYProfileFilter> yFilters)
        {
            _terrain = terrain;
            _palette = palette;
            _yFilters = yFilters;
        }

        public static IReadOnlyList<IRibbonYProfileFilter> DefaultYFilters() => new IRibbonYProfileFilter[]
        {
            new MovingAverageFilter(TrackPieceConstants.RibbonYSmoothKernel),
            new MinClampLiftFilter()
        };

        public Mesh Build(IReadOnlyList<TrackChainAnchor> chain, string meshName)
        {
            if (chain == null || chain.Count < 2) return null;
            var samples = CatmullRomSpline.Resample(chain, TrackPieceConstants.RibbonSampleArcStep);
            int n = samples.Count;
            if (n < 2) return null;

            var rights = new Vector3[n];
            var halfWidths = new float[n];
            var centerY = new float[n];
            var leftOuterY = new float[n];
            var rightOuterY = new float[n];

            for (int i = 0; i < n; i++)
            {
                var s = samples[i];
                Vector3 tan = s.Tangent; tan.y = 0f;
                if (tan.sqrMagnitude < 1e-6f) tan = Vector3.forward;
                tan.Normalize();
                Vector3 right = Vector3.Cross(Vector3.up, tan);
                if (right.sqrMagnitude < 1e-6f) right = Vector3.right;
                right.Normalize();
                rights[i] = right;
                halfWidths[i] = s.HalfWidth;

                Vector3 center = s.Position;
                centerY[i] = SampleTerrainY(center.x, center.z);
                Vector3 leftOuter = center - right * (s.HalfWidth + TrackPieceConstants.ApronWidth);
                Vector3 rightOuter = center + right * (s.HalfWidth + TrackPieceConstants.ApronWidth);
                leftOuterY[i] = SampleTerrainY(leftOuter.x, leftOuter.z);
                rightOuterY[i] = SampleTerrainY(rightOuter.x, rightOuter.z);
            }

            ApplyYFilters(centerY);

            float roadLift = TrackPieceConstants.SlabTopY + TrackPieceConstants.RoadLift;

            int colsPerSide = TrackPieceConstants.ApronSubdivisions;
            int sideVerts = colsPerSide + 1; // index 0 = road edge, index N = apron outer
            var leftGrid = new Vector3[n, sideVerts];
            var rightGrid = new Vector3[n, sideVerts];

            for (int i = 0; i < n; i++)
            {
                Vector3 center = samples[i].Position;
                Vector3 right = rights[i];
                float hw = halfWidths[i];
                float roadY = centerY[i] + roadLift;
                float leftOY = leftOuterY[i];
                float rightOY = rightOuterY[i];

                for (int j = 0; j <= colsPerSide; j++)
                {
                    float t = (float)j / colsPerSide;     // 0 at road edge, 1 at apron outer
                    float widthOff = hw + TrackPieceConstants.ApronWidth * t;
                    float blend = SmoothStep01(t);        // S-curve fillet
                    Vector3 lp = center - right * widthOff;
                    float ly = Mathf.Lerp(roadY, leftOY, blend);
                    leftGrid[i, j] = new Vector3(lp.x, ly, lp.z);
                    Vector3 rp = center + right * widthOff;
                    float ry = Mathf.Lerp(roadY, rightOY, blend);
                    rightGrid[i, j] = new Vector3(rp.x, ry, rp.z);
                }
            }

            _buffer.Clear();
            Color road = _palette.Road;
            Color edge = _palette.RoadEdge;
            Color apron = _palette.UnderDeck;

            for (int i = 0; i < n - 1; i++)
            {
                MeshPrimitives.AddQuad(_buffer,
                    leftGrid[i, 0], leftGrid[i + 1, 0],
                    rightGrid[i + 1, 0], rightGrid[i, 0],
                    road);

                for (int j = 0; j < colsPerSide; j++)
                {
                    float u = colsPerSide > 1 ? (float)j / (colsPerSide - 1) : 0f;
                    Color c = Color.Lerp(edge, apron, u);

                    MeshPrimitives.AddQuad(_buffer,
                        leftGrid[i, j + 1], leftGrid[i + 1, j + 1],
                        leftGrid[i + 1, j], leftGrid[i, j],
                        c);

                    MeshPrimitives.AddQuad(_buffer,
                        rightGrid[i, j], rightGrid[i + 1, j],
                        rightGrid[i + 1, j + 1], rightGrid[i, j + 1],
                        c);
                }
            }

            return _buffer.ToMesh(meshName);
        }

        private void ApplyYFilters(float[] working)
        {
            if (_yFilters == null || _yFilters.Count == 0) return;
            var raw = new float[working.Length];
            Array.Copy(working, raw, working.Length);
            for (int i = 0; i < _yFilters.Count; i++)
                _yFilters[i].Apply(raw, working);
        }

        private float SampleTerrainY(float x, float z)
        {
            if (_terrain == null || !_terrain.IsInitialized) return 0f;
            return _terrain.SampleHeight(x, z);
        }

        private static float SmoothStep01(float t)
        {
            t = Mathf.Clamp01(t);
            return t * t * (3f - 2f * t);
        }
    }
}
