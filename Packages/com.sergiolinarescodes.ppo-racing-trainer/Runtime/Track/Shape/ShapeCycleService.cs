using Unidad.Core.EventBus;

namespace UnityPpoRacingTrainer.Core.Track.Shape
{
    internal sealed class ShapeCycleService : IShapeCycleService
    {
        private readonly IEventBus _eventBus;
        private readonly ITrackShapeCatalog _catalog;

        public ShapeCycleService(IEventBus eventBus, ITrackShapeCatalog catalog)
        {
            _eventBus = eventBus;
            _catalog = catalog;
            CurrentIndex = 0;
            Facing = TrackDirection.North;
        }

        public TrackShape Current => _catalog.Count == 0 ? null : _catalog.All[CurrentIndex];
        public int CurrentIndex { get; private set; }
        public TrackDirection Facing { get; private set; }

        public void Next()
        {
            if (_catalog.Count == 0) return;
            CurrentIndex = (CurrentIndex + 1) % _catalog.Count;
            Publish();
        }

        public void Previous()
        {
            if (_catalog.Count == 0) return;
            CurrentIndex = (CurrentIndex - 1 + _catalog.Count) % _catalog.Count;
            Publish();
        }

        // R = 90° step (cardinal-preserving). Compound shapes today are built from
        // cardinal pieces, so a diagonal facing would render every cardinal slab
        // tilted 45° — the chain extractor's port-quantize step would still match,
        // but the rendered geometry looks broken. Stay on cardinals for the cycle;
        // diagonal-piece placement uses hand-authored facings.
        public void RotateRight() => Facing = Facing.RotateRight();
        public void RotateLeft() => Facing = Facing.RotateLeft();

        private void Publish() =>
            _eventBus.Publish(new TrackShapeSelectedEvent(Current.Id, CurrentIndex));
    }
}
