using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.Terrain;
using UnityPpoRacingTrainer.Core.Track.Loop;
using Unidad.Core.EventBus;
using Unidad.Core.Systems;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Track.Topology
{
    internal sealed class TrackEndingService : SystemServiceBase, ITrackEndingService
    {
        private readonly ITrackPlacementService _placement;
        private readonly ITrackPieceCatalog _catalog;
        private readonly IClosedLoopService _loop;
        private readonly ITerrainService _terrain;

        private readonly OpenPortIndex _index = new();
        private readonly List<OpenPort> _ends = new();
        private bool _isClosedLoop;

        private Vector3 _startLinePos;
        private float _startLineHeading;
        private bool _hasStartLine;

        public TrackEndingService(
            IEventBus eventBus,
            ITrackPlacementService placement,
            ITrackPieceCatalog catalog,
            IClosedLoopService loop,
            ITerrainService terrain = null) : base(eventBus)
        {
            _placement = placement;
            _catalog = catalog;
            _loop = loop;
            _terrain = terrain;
            Subscribe<TrackPiecePlacedEvent>(_ => Rebuild());
            Subscribe<TrackPieceRemovedEvent>(_ => Rebuild());
            Rebuild();
        }

        public IReadOnlyList<OpenPort> OpenEnds => _ends;
        public bool IsClosedLoop => _isClosedLoop;

        public bool TryGetStartLine(out Vector3 worldPos, out float heading)
        {
            worldPos = _startLinePos;
            heading = _startLineHeading;
            return _hasStartLine;
        }

        public void SetStartLine(Vector3 worldPos, float heading)
        {
            _startLinePos = worldPos;
            _startLineHeading = heading;
            _hasStartLine = true;
        }

        private void Rebuild()
        {
            float cellSize = _terrain != null && _terrain.IsInitialized ? _terrain.CellSize : 1f;
            _index.Rebuild(_placement.Placed, _catalog, cellSize);

            bool prevClosed = _isClosedLoop;
            _isClosedLoop = _loop.IsLoopClosed;

            bool changed = prevClosed != _isClosedLoop || !SameSet(_ends, _index.All);
            if (!changed) return;

            _ends.Clear();
            for (int i = 0; i < _index.All.Count; i++) _ends.Add(_index.All[i]);

            Publish(new TrackTopologyChangedEvent(_ends.Count, _isClosedLoop));
        }

        // Order-insensitive equality by membership. Linear scan is fine —
        // open-end counts rarely exceed a handful on a strip.
        private static bool SameSet(List<OpenPort> a, IReadOnlyList<OpenPort> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                bool found = false;
                for (int j = 0; j < b.Count; j++)
                {
                    if (a[i].Equals(b[j])) { found = true; break; }
                }
                if (!found) return false;
            }
            return true;
        }
    }
}
