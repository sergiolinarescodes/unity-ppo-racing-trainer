using Unidad.Core.Grid;

namespace UnityPpoRacingTrainer.Core.Track.Shape
{
    /// <summary>
    /// Shape-local offset from the shape's anchor tile. Rotated through the shape's
    /// world facing via <see cref="TrackPieceFootprint.RotateOffset"/> so a single
    /// canonical (north-facing) layout produces the four cardinal variants without
    /// duplicating data.
    /// </summary>
    public readonly record struct GridOffset(int Dx, int Dz)
    {
        public static GridOffset Zero => new(0, 0);

        public GridPosition Apply(GridPosition origin, TrackDirection shapeFacing)
        {
            var (rdx, rdz) = TrackPieceFootprint.RotateOffset(Dx, Dz, shapeFacing);
            return new GridPosition(origin.X + rdx, origin.Y + rdz);
        }
    }
}
