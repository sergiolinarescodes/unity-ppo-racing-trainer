using System.Collections.Generic;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Track
{
    /// <summary>
    /// Dispatches to per-family <see cref="ITrackShapeMeshStrategy"/> implementations and
    /// optionally applies a height adapter to every emitted vertex (currently a no-op
    /// for <see cref="FlatHeightAdapter"/>; v2 will snap to terrain corner heights).
    /// Caches built results by <see cref="TrackPieceShape"/>.
    /// </summary>
    internal sealed class TrackPieceMeshBuilder : ITrackPieceMeshBuilder
    {
        private readonly Dictionary<TrackPieceFamily, ITrackShapeMeshStrategy> _strategies;
        // Cache keyed by (shape, variant) so different wall configurations of the
        // same shape produce distinct Mesh assets. Default-variant placements still share
        // a single mesh because every legacy seeder resolves to TrackPieceVariantId.Default.
        private readonly Dictionary<(TrackPieceShape, TrackPieceVariantId), MeshBuildResult> _cache = new();
        private readonly ITrackHeightAdapter _heightAdapter;
        private readonly MeshBuffer _buffer = new();

        public TrackPieceMeshBuilder(ITrackHeightAdapter heightAdapter)
        {
            _heightAdapter = heightAdapter;
            _strategies = new Dictionary<TrackPieceFamily, ITrackShapeMeshStrategy>
            {
                { TrackPieceFamily.Straight, new StraightMeshStrategy() },
                { TrackPieceFamily.Curve, new CurveMeshStrategy() },
                { TrackPieceFamily.Ramp, new RampMeshStrategy() },
                { TrackPieceFamily.DiagonalStraight, new DiagonalStraightMeshStrategy() },
                { TrackPieceFamily.DiagonalCurve, new DiagonalCurveMeshStrategy() },
            };
        }

        public MeshBuildResult Build(TrackPieceDefinition def, TrackPalette palette)
            => Build(def, palette, TrackPieceVariantId.Default);

        public MeshBuildResult Build(TrackPieceDefinition def, TrackPalette palette, TrackPieceVariantId variantId)
        {
            var cacheKey = (def.Shape, variantId);
            if (_cache.TryGetValue(cacheKey, out var cached) && cached.Mesh != null)
                return cached;

            if (!_strategies.TryGetValue(def.Family, out var strategy))
                throw new System.InvalidOperationException(
                    $"No mesh strategy registered for family {def.Family}");

            // Variant indirection: if the piece declares named variants, the strategy
            // sees a def whose Edges are swapped to the active variant + the
            // active wall-shoulder is staged on the buffer. Strategies remain
            // unaware of variants — they read def.Edges + buf.WallShoulder.
            var effectiveDef = def;
            var shoulderMode = WallShoulderMode.Near;
            if (def.HasVariants)
            {
                var v = def.GetVariant(variantId);
                effectiveDef = def with { Edges = v.Edges };
                shoulderMode = v.WallShoulderMode;
            }

            _buffer.Clear();
            _buffer.WallShoulder = TrackPieceConstants.ShoulderFor(shoulderMode);
            strategy.Build(_buffer, effectiveDef, palette);

            RecenterAndApplyHeight(effectiveDef);

            var nameSuffix = variantId.Index == 0 ? string.Empty : $"_v{variantId.Index}";
            var mesh = _buffer.ToMesh($"TrackPiece_{def.Shape.Id}{nameSuffix}");
            var walls = _buffer.Walls.Count == 0
                ? System.Array.Empty<WallSegment>()
                : _buffer.Walls.ToArray();
            var barriers = _buffer.WallBarriers.Count == 0
                ? System.Array.Empty<WallBarrierPlacement>()
                : _buffer.WallBarriers.ToArray();
            var result = new MeshBuildResult(mesh, walls, barriers);
            _cache[cacheKey] = result;
            return result;
        }

        /// <summary>
        /// Strategies emit mesh in canonical local space where the SW corner of the
        /// anchor tile sits at (0, 0). For the GameObject's transform.rotation to rotate
        /// the piece around the anchor tile's CENTER (so footprint cells match what the
        /// validator computes), we shift every vertex by (−0.5, 0, −0.5) here. The
        /// placement service then sets transform.position to the world center of the
        /// anchor tile. Height adapter is also applied in this single pass.
        ///
        /// Pieces declared with <see cref="TrackPieceDefinition.MirrorX"/> have their
        /// X mirrored around the tile center after recentering — that's how a left-turn
        /// curve reuses the right-turn curve mesh strategy. Mirroring inverts triangle
        /// winding, so we flip every triangle's vertex order and every normal's X.
        ///
        /// Wall segments + kerb zones go through the same recenter+mirror so collision
        /// stays aligned with the visible mesh.
        /// </summary>
        private void RecenterAndApplyHeight(TrackPieceDefinition def)
        {
            const float halfTile = 0.5f;
            float xSign = def.MirrorX ? -1f : 1f;

            for (int i = 0; i < _buffer.Vertices.Count; i++)
            {
                var v = _buffer.Vertices[i];
                float x = (v.x - halfTile) * xSign;
                float z = v.z - halfTile;
                float y = v.y;
                if (_heightAdapter != null)
                {
                    float dy = _heightAdapter.SampleHeight(def, new Vector2(x, z));
                    y += dy;
                }
                _buffer.Vertices[i] = new Vector3(x, y, z);
            }

            // Walls — transform endpoints. Owner stays default; placement stamps the id.
            for (int i = 0; i < _buffer.Walls.Count; i++)
            {
                var w = _buffer.Walls[i];
                Vector2 a = TransformXZ(w.A, xSign, halfTile);
                Vector2 b = TransformXZ(w.B, xSign, halfTile);
                _buffer.Walls[i] = w with { A = a, B = b };
            }

            // Wall barrier placements — recenter + mirror in lockstep with the mesh.
            for (int i = 0; i < _buffer.WallBarriers.Count; i++)
            {
                var b = _buffer.WallBarriers[i];
                var c = TransformXZ(b.CenterXZ, xSign, halfTile);
                var f = new Vector2(b.ForwardXZ.x * xSign, b.ForwardXZ.y);
                _buffer.WallBarriers[i] = b with { CenterXZ = c, ForwardXZ = f };
            }

            if (def.MirrorX)
            {
                for (int i = 0; i < _buffer.Normals.Count; i++)
                {
                    var n = _buffer.Normals[i];
                    _buffer.Normals[i] = new Vector3(-n.x, n.y, n.z);
                }
                for (int i = 0; i + 2 < _buffer.Triangles.Count; i += 3)
                {
                    int b = _buffer.Triangles[i + 1];
                    _buffer.Triangles[i + 1] = _buffer.Triangles[i + 2];
                    _buffer.Triangles[i + 2] = b;
                }
            }
        }

        private static Vector2 TransformXZ(Vector2 xz, float xSign, float halfTile)
        {
            float x = (xz.x - halfTile) * xSign;
            float z = xz.y - halfTile;
            return new Vector2(x, z);
        }
    }
}
