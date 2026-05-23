using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.Terrain;
using Unidad.Core.Grid;

namespace UnityPpoRacingTrainer.Core.Track
{
    /// <summary>
    /// Bundle of state passed to every validator for one placement attempt. Validators
    /// are stateless; all dependencies flow through this struct.
    /// </summary>
    public sealed class PlacementContext
    {
        public TrackPieceDefinition Definition { get; }
        public GridPosition Origin { get; }
        public TrackDirection Facing { get; }
        public ITerrainService Terrain { get; }
        public IReadOnlyDictionary<GridPosition, TrackPieceId> Occupancy { get; }

        public PlacementContext(
            TrackPieceDefinition definition,
            GridPosition origin,
            TrackDirection facing,
            ITerrainService terrain,
            IReadOnlyDictionary<GridPosition, TrackPieceId> occupancy)
        {
            Definition = definition;
            Origin = origin;
            Facing = facing;
            Terrain = terrain;
            Occupancy = occupancy;
        }
    }
}
