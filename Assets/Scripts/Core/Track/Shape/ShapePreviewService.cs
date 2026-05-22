using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.Terrain;
using Unidad.Core.Grid;

namespace UnityPpoRacingTrainer.Core.Track.Shape
{
    /// <summary>
    /// Runs the same validator pipeline as <see cref="TrackPlacementService"/>, but
    /// against an in-memory snapshot of the live occupancy map. Pieces are validated
    /// in shape-list order; each accepted piece's footprint is added to the snapshot
    /// before the next piece runs, so two pieces in the same shape correctly conflict.
    /// Commit (<see cref="ShapePlacementService"/>) iterates the same order, keeping
    /// preview and commit semantically aligned.
    /// </summary>
    internal sealed class ShapePreviewService : IShapePreviewService
    {
        private readonly ITrackPieceCatalog _pieces;
        private readonly IReadOnlyList<ITrackPlacementValidator> _validators;
        private readonly ITerrainService _terrain;
        private readonly ITrackPlacementService _placement;

        public ShapePreviewService(
            ITrackPieceCatalog pieces,
            IReadOnlyList<ITrackPlacementValidator> validators,
            ITerrainService terrain,
            ITrackPlacementService placement)
        {
            _pieces = pieces;
            _validators = validators;
            _terrain = terrain;
            _placement = placement;
        }

        public ShapePreviewResult Compute(TrackShape shape, GridPosition origin, TrackDirection facing)
        {
            var snapshot = new Dictionary<GridPosition, TrackPieceId>(_placement.Occupancy);
            var pieces = new List<PiecePreview>(shape.Pieces.Count);
            bool allValid = true;

            for (int i = 0; i < shape.Pieces.Count; i++)
            {
                var sp = shape.Pieces[i];
                var tile = sp.Offset.Apply(origin, facing);
                var resolvedFacing = sp.ResolveFacing(facing);

                if (!_pieces.TryGet(sp.PieceType, out var def))
                {
                    pieces.Add(new PiecePreview(sp.PieceType, tile, resolvedFacing, false,
                        $"Unknown piece '{sp.PieceType.Id}'", sp.VariantOverride));
                    allValid = false;
                    continue;
                }

                var ctx = new PlacementContext(def, tile, resolvedFacing, _terrain, snapshot);

                bool valid = true;
                string reason = null;
                for (int v = 0; v < _validators.Count; v++)
                {
                    var r = _validators[v].Validate(ctx);
                    if (!r.IsValid)
                    {
                        valid = false;
                        reason = r.Reason;
                        break;
                    }
                }

                pieces.Add(new PiecePreview(sp.PieceType, tile, resolvedFacing, valid, reason, sp.VariantOverride));

                if (valid)
                {
                    var fakeId = TrackPieceId.New();
                    foreach (var t in def.Footprint.Tiles(tile, resolvedFacing))
                        snapshot[t] = fakeId;
                }
                else
                {
                    allValid = false;
                }
            }

            return new ShapePreviewResult(origin, facing, pieces, allValid);
        }
    }
}
