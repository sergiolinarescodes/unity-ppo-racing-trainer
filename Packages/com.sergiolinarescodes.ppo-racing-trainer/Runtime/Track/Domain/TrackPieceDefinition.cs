using System;
using System.Collections.Generic;

namespace UnityPpoRacingTrainer.Core.Track
{
    /// <summary>
    /// Static, read-only description of one catalog piece. Mesh strategies and
    /// validators read from this; placed instances reference it via <see cref="Shape"/>.
    /// <para>
    /// <b>Variants:</b> the constructor's <see cref="Edges"/> remains the legacy
    /// default-variant data so every existing seeder and consumer keeps working
    /// unchanged. Pieces that want multiple wall/kerb configurations on
    /// the same footprint set <see cref="Variants"/> via record <c>with</c>; the
    /// V-key cycles those at placement time. <see cref="GetVariant"/> returns the
    /// effective edges for a given id, falling back to the synthesised default
    /// variant when no list is set.
    /// </para>
    /// </summary>
    public sealed record TrackPieceDefinition(
        TrackPieceShape Shape,
        TrackPieceFamily Family,
        TrackPieceDimensions Dimensions,
        TrackPieceFootprint Footprint,
        IReadOnlyList<TrackPort> Ports,
        bool HasPianos,
        float CurveCenterRadius,
        TerrainShapeMask AllowedTerrain,
        IReadOnlyList<EdgeMarker> Edges,
        bool MirrorX = false)
    {
        /// <summary>
        /// Optional named alternates of edges for the same footprint. Null or empty
        /// = single-variant piece; <see cref="Edges"/> is authoritative.
        /// </summary>
        public IReadOnlyList<TrackPieceVariant> Variants { get; init; }

        /// <summary>Default variant id used when placement does not specify one.</summary>
        public TrackPieceVariantId DefaultVariantId { get; init; } = TrackPieceVariantId.Default;

        /// <summary>True when more than one named variant is registered.</summary>
        public bool HasVariants => Variants is { Count: > 0 };

        /// <summary>Total number of variants this piece exposes for cycling (1 if none registered).</summary>
        public int VariantCount => HasVariants ? Variants.Count : 1;

        /// <summary>
        /// Returns the variant for the given id. When <see cref="Variants"/> is
        /// unset, returns a synthesised view over <see cref="Edges"/>. Out-of-range
        /// ids clamp to index 0.
        /// </summary>
        public TrackPieceVariant GetVariant(TrackPieceVariantId id)
        {
            if (HasVariants)
            {
                int i = id.Index;
                if (i < 0 || i >= Variants.Count) i = 0;
                return Variants[i];
            }
            return new TrackPieceVariant(
                TrackPieceVariantId.Default,
                "Default",
                Edges ?? Array.Empty<EdgeMarker>(),
                WallShoulderMode.Near);
        }
    }

    /// <summary>
    /// How far walls sit from the road carriageway. Selectable per variant so the
    /// same shape can expose tight-walled vs distance-walled options through the
    /// V-key cycle.
    /// </summary>
    public enum WallShoulderMode : byte
    {
        Near,
        Mid
    }

    /// <summary>Variant slot id; index into <see cref="TrackPieceDefinition.Variants"/>.</summary>
    public readonly record struct TrackPieceVariantId(byte Index)
    {
        public static TrackPieceVariantId Default => default;
        public TrackPieceVariantId Next(int total) => new((byte)((Index + 1) % Math.Max(1, total)));
    }

    /// <summary>
    /// Per-shape "currently selected variant" state. Driven by the V-key in
    /// <see cref="UnityPpoRacingTrainer.Core.Track.Scenarios"/> placement scenarios
    /// and the track editor's anchor-popup edits. State is per-shape so cycling V on a
    /// curve, then Tab to a straight, then back to the curve restores the curve's last
    /// variant — which is the natural UX for picking and re-picking the same card.
    /// </summary>
    public interface IVariantCycleService
    {
        /// <summary>Currently selected variant for the given shape (defaults to Default).</summary>
        TrackPieceVariantId Current(TrackPieceShape shape);

        /// <summary>Number of variants registered on the shape (1 if shape lacks a variants list).</summary>
        int CountFor(TrackPieceShape shape);

        /// <summary>Advance to the next variant of the shape, wrapping at the end. Returns the new id.</summary>
        TrackPieceVariantId Cycle(TrackPieceShape shape);

        /// <summary>Force the active variant for a shape (used by the editor to apply explicit picks).</summary>
        void Set(TrackPieceShape shape, TrackPieceVariantId variantId);
    }

    /// <summary>
    /// Default <see cref="IVariantCycleService"/> backed by a per-shape dictionary.
    /// Variant counts come from <see cref="ITrackPieceCatalog"/> so newly registered
    /// authored pieces participate in cycling automatically.
    /// </summary>
    internal sealed class VariantCycleService : IVariantCycleService
    {
        private readonly ITrackPieceCatalog _catalog;
        private readonly Dictionary<TrackPieceShape, TrackPieceVariantId> _state = new();

        public VariantCycleService(ITrackPieceCatalog catalog)
        {
            _catalog = catalog;
        }

        public TrackPieceVariantId Current(TrackPieceShape shape)
            => _state.TryGetValue(shape, out var v) ? v : TrackPieceVariantId.Default;

        public int CountFor(TrackPieceShape shape)
            => _catalog != null && _catalog.TryGet(shape, out var def) ? def.VariantCount : 1;

        public TrackPieceVariantId Cycle(TrackPieceShape shape)
        {
            int total = CountFor(shape);
            var next = Current(shape).Next(total);
            _state[shape] = next;
            return next;
        }

        public void Set(TrackPieceShape shape, TrackPieceVariantId variantId)
        {
            _state[shape] = variantId;
        }
    }

    /// <summary>
    /// One named alternate of edges for a piece footprint. Variants share
    /// the parent definition's footprint, ports, and terrain mask — only the
    /// visible/collision decoration differs (walls, kerbs, wall shoulder distance).
    /// </summary>
    public sealed record TrackPieceVariant(
        TrackPieceVariantId Id,
        string DisplayName,
        IReadOnlyList<EdgeMarker> Edges,
        WallShoulderMode WallShoulderMode = WallShoulderMode.Near);

    /// <summary>Surface kind for a piece-perimeter marker. Static kerbs were removed —
    /// kerbs are now dynamically placed by the racing-line kerb service during the
    /// ghost-loop preview, not baked into piece definitions.</summary>
    public enum EdgeKind : byte
    {
        Wall
    }

    /// <summary>
    /// Identifies which 2D edge in canonical local space an <see cref="EdgeMarker"/> covers.
    /// Anchor names are relative to the piece's canonical (north-facing) orientation.
    /// </summary>
    public enum EdgeAnchor : byte
    {
        StraightWest,
        StraightEast,
        StraightSouth,
        StraightNorth,
        ArcInner,
        ArcOuter,
        DiagonalLeft,
        DiagonalRight,
        RampWest,
        RampEast
    }

    /// <summary>
    /// Per-side perimeter marker on a track piece. Mesh strategies emit visible
    /// walls/kerbs from these; the collision system materialises wall segments
    /// + kerb zones in lockstep. <c>StartT</c>/<c>EndT</c> ∈ [0,1] support
    /// partial-edge markers (a kerb on only the apex of an arc, etc).
    /// </summary>
    public readonly record struct EdgeMarker(
        EdgeAnchor Anchor,
        EdgeKind Kind,
        int TileIndex = 0,
        int LaneIndex = 0,
        float StartT = 0f,
        float EndT = 1f);

}
