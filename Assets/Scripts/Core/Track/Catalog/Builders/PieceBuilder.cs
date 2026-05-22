using System;
using System.Collections.Generic;

namespace UnityPpoRacingTrainer.Core.Track.Catalog.Builders
{
    /// <summary>
    /// Fluent builder for <see cref="TrackPieceDefinition"/>. Replaces the constructor's
    /// 9 positional arguments — clarifies which fields are set and lets new piece
    /// variants read like a small DSL. Required: shape, family, dimensions. Footprint
    /// defaults to match dimensions; everything else is opt-in.
    /// <para>
    /// <b>Variants:</b> single-variant seeders chain <see cref="Wall"/> / <see cref="AutoBarriers"/>.
    /// Multi-variant pieces add named alternates via <see cref="Variant"/> after the default
    /// edges are configured. Static kerbs are deprecated — the dynamic racing-line kerb
    /// service places them during the ghost-loop preview based on where the car drifts.
    /// </para>
    /// </summary>
    public sealed class PieceBuilder
    {
        private readonly TrackPieceShape _shape;
        private readonly TrackPieceFamily _family;
        private TrackPieceDimensions _dimensions;
        private TrackPieceFootprint _footprint;
        private readonly List<TrackPort> _ports = new();
        private readonly List<EdgeMarker> _edges = new();
        private readonly List<TrackPieceVariant> _variants = new();
        private bool _hasPianos;
        private float _curveRadius;
        private TerrainShapeMask _allowedTerrain = TerrainShapeMask.FlatOnly;
        private bool _mirrorX;

        public PieceBuilder(TrackPieceShape shape, TrackPieceFamily family, int width, int length)
        {
            _shape = shape;
            _family = family;
            _dimensions = new TrackPieceDimensions(width, length);
            _footprint = new TrackPieceFootprint(width, length);
        }

        public PieceBuilder Footprint(int width, int length)
        {
            _footprint = new TrackPieceFootprint(width, length);
            return this;
        }

        public PieceBuilder Port(TrackDirection side, int lane, TrackPortState state)
        {
            _ports.Add(new TrackPort(side, lane, state));
            return this;
        }

        /// <summary>Convenience: a road-state port at the given side and lane.</summary>
        public PieceBuilder Road(TrackDirection side, int lane = 0)
            => Port(side, lane, TrackPortState.Road);

        public PieceBuilder Pianos()
        {
            _hasPianos = true;
            return this;
        }

        public PieceBuilder CurveRadius(float radius)
        {
            _curveRadius = radius;
            return this;
        }

        public PieceBuilder AllowedTerrain(TerrainShapeMask mask)
        {
            _allowedTerrain = mask;
            return this;
        }

        public PieceBuilder MirrorX()
        {
            _mirrorX = true;
            return this;
        }

        public PieceBuilder Wall(EdgeAnchor anchor, int tile = 0, int lane = 0, float t0 = 0f, float t1 = 1f)
        {
            _edges.Add(new EdgeMarker(anchor, EdgeKind.Wall, tile, lane, t0, t1));
            return this;
        }

        /// <summary>
        /// Register a named alternate of edges for the same footprint. The V-key
        /// cycles through these at placement time. Call after the default variant's
        /// <see cref="Wall"/> chain so index 0 matches the legacy single-variant geometry.
        /// </summary>
        public PieceBuilder Variant(string displayName, Action<VariantBuilder> configure)
        {
            if (configure == null) throw new ArgumentNullException(nameof(configure));
            var vb = new VariantBuilder();
            configure(vb);
            var id = new TrackPieceVariantId((byte)_variants.Count);
            _variants.Add(new TrackPieceVariant(id, displayName ?? string.Empty, vb.BuildEdges(), vb.WallShoulderMode));
            return this;
        }

        /// <summary>
        /// Default per-family barrier pattern. Straights get walls on both long edges.
        /// Curves get an outer wall only — the inner (apex-side) wall was dropped so racing
        /// lines can clip the interior corner without colliding. Static kerbs are not
        /// emitted here; the dynamic racing-line kerb service places them at runtime where
        /// the ghost car drifts. Diagonals + Ramps no-op (Phase 2). Multi-tile pieces need
        /// explicit Wall calls — AutoBarriers only handles the single-tile case.
        /// </summary>
        public PieceBuilder AutoBarriers()
        {
            switch (_family)
            {
                case TrackPieceFamily.Straight:
                    _edges.Add(new EdgeMarker(EdgeAnchor.StraightWest, EdgeKind.Wall));
                    _edges.Add(new EdgeMarker(EdgeAnchor.StraightEast, EdgeKind.Wall));
                    break;
                case TrackPieceFamily.Curve:
                    if (_dimensions.Length == 1 && _dimensions.Width == 1)
                    {
                        _edges.Add(new EdgeMarker(EdgeAnchor.ArcOuter, EdgeKind.Wall));
                    }
                    break;
            }
            return this;
        }

        public TrackPieceDefinition Build()
        {
            // Variants list contract: index 0 mirrors the legacy default-variant
            // edges so existing mesh strategies see identical geometry until V-key
            // cycles past index 0.
            IReadOnlyList<TrackPieceVariant> variants = null;
            if (_variants.Count > 0)
            {
                var combined = new List<TrackPieceVariant>(_variants.Count + 1)
                {
                    new(TrackPieceVariantId.Default, "Default", _edges.ToArray(), WallShoulderMode.Near)
                };
                for (int i = 0; i < _variants.Count; i++)
                {
                    var v = _variants[i];
                    combined.Add(new TrackPieceVariant(new TrackPieceVariantId((byte)(i + 1)), v.DisplayName, v.Edges, v.WallShoulderMode));
                }
                variants = combined;
            }

            return new TrackPieceDefinition(
                Shape: _shape,
                Family: _family,
                Dimensions: _dimensions,
                Footprint: _footprint,
                Ports: _ports,
                HasPianos: _hasPianos,
                CurveCenterRadius: _curveRadius,
                AllowedTerrain: _allowedTerrain,
                Edges: _edges,
                MirrorX: _mirrorX)
            {
                Variants = variants,
            };
        }
    }

    /// <summary>
    /// Sub-builder used inside <see cref="PieceBuilder.Variant"/> to accumulate edges
    /// for a named variant. Mirrors the parent builder's edge DSL plus a wall-shoulder
    /// selector so a variant can be authored as walls-near or walls-mid.
    /// </summary>
    public sealed class VariantBuilder
    {
        private readonly List<EdgeMarker> _edges = new();

        public WallShoulderMode WallShoulderMode { get; private set; } = WallShoulderMode.Near;

        public VariantBuilder Wall(EdgeAnchor anchor, int tile = 0, int lane = 0, float t0 = 0f, float t1 = 1f)
        {
            _edges.Add(new EdgeMarker(anchor, EdgeKind.Wall, tile, lane, t0, t1));
            return this;
        }

        /// <summary>Walls almost-touching the road carriageway. Default.</summary>
        public VariantBuilder WallsNear()
        {
            WallShoulderMode = WallShoulderMode.Near;
            return this;
        }

        /// <summary>Walls visibly offset from the road but still in-tile.</summary>
        public VariantBuilder WallsMid()
        {
            WallShoulderMode = WallShoulderMode.Mid;
            return this;
        }

        internal IReadOnlyList<EdgeMarker> BuildEdges() => _edges.ToArray();
    }
}
