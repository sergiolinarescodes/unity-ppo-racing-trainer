using System.Collections.Generic;
using System.Linq;
using Unidad.Core.Registry;

namespace UnityPpoRacingTrainer.Core.Track
{
    internal sealed class TrackPieceCatalog : RegistryBase<TrackPieceShape, TrackPieceDefinition>, ITrackPieceCatalog
    {
        private List<TrackPieceDefinition> _allCache;

        public IReadOnlyList<TrackPieceDefinition> All
        {
            get
            {
                if (_allCache == null || _allCache.Count != Count)
                    _allCache = Values.ToList();
                return _allCache;
            }
        }

        public new TrackPieceDefinition Get(TrackPieceShape shape) => base.Get(shape);

        public new bool TryGet(TrackPieceShape shape, out TrackPieceDefinition def) =>
            base.TryGet(shape, out def);

        public new bool Has(TrackPieceShape shape) => base.Has(shape);

        public void Register(TrackPieceDefinition def)
        {
            base.Register(def.Shape, def);
            _allCache = null;
        }
    }
}
