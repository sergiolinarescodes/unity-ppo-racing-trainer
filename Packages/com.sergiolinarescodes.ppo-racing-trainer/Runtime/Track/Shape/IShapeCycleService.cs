namespace UnityPpoRacingTrainer.Core.Track.Shape
{
    /// <summary>
    /// Tab-cycle state for the active shape preview. v1 fixes <see cref="Facing"/>
    /// to <see cref="TrackDirection.North"/>; rotation hooks are present so v2 can
    /// bind <c>Q</c>/<c>E</c> without changing the API.
    /// </summary>
    public interface IShapeCycleService
    {
        TrackShape Current { get; }
        int CurrentIndex { get; }
        TrackDirection Facing { get; }

        void Next();
        void Previous();
        void RotateRight();
        void RotateLeft();
    }
}
