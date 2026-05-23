using System.Collections.Generic;
using System.Linq;
using Unidad.Core.Registry;

namespace UnityPpoRacingTrainer.Core.Track.Shape
{
    internal sealed class TrackShapeCatalog : RegistryBase<TrackShapeId, TrackShape>, ITrackShapeCatalog
    {
        private List<TrackShape> _allCache;

        public IReadOnlyList<TrackShape> All
        {
            get
            {
                if (_allCache == null || _allCache.Count != Count)
                    _allCache = Values.ToList();
                return _allCache;
            }
        }

        public new TrackShape Get(TrackShapeId id) => base.Get(id);

        public new bool TryGet(TrackShapeId id, out TrackShape shape) =>
            base.TryGet(id, out shape);

        public new bool Has(TrackShapeId id) => base.Has(id);

        public void Register(TrackShape shape)
        {
            base.Register(shape.Id, shape);
            _allCache = null;
        }
    }
}
