using Unidad.Core.Grid;

namespace UnityPpoRacingTrainer.Core.Track
{
    /// <summary>
    /// Tracks frame-to-frame placement state: the active shape index, its facing,
    /// the cursor origin, whether the cursor is off-terrain, and whether the magnet
    /// is currently engaged. Exposes a single <see cref="IsDirty"/> flag the caller
    /// flips on whenever it needs to refresh ghost geometry.
    ///
    /// Lifts the dirty-flag bookkeeping out of the placement scenario so the scenario
    /// becomes a thin coordinator. Future expansion: emit explicit transition events
    /// (Hovering→Snapped→Confirmed) for undo/replay or analytics.
    /// </summary>
    internal sealed class PlacementStateMachine
    {
        private GridPosition _origin;
        private int _shapeIndex = -1;
        private TrackDirection _facing;
        private bool _offTerrain = true;
        private bool _magnet;

        public bool MagnetActive => _magnet;
        public bool OffTerrain => _offTerrain;

        /// <summary>
        /// Push a new frame of inputs. Returns true if anything changed since the last
        /// frame (the caller should rebuild ghosts). The magnet flag is tracked here so
        /// callers can also detect engage/release transitions via
        /// <see cref="MagnetTransitioned"/>.
        /// </summary>
        public bool Update(GridPosition origin, int shapeIndex, TrackDirection facing, bool offTerrain, bool magnetActive)
        {
            bool dirty = offTerrain != _offTerrain
                         || origin != _origin
                         || shapeIndex != _shapeIndex
                         || facing != _facing
                         || magnetActive != _magnet;

            _origin = origin;
            _shapeIndex = shapeIndex;
            _facing = facing;
            _offTerrain = offTerrain;
            _magnet = magnetActive;
            return dirty;
        }

        /// <summary>
        /// Force the next <see cref="Update"/> call to report dirty (e.g. after the
        /// occupancy map changed under us, so the same origin could now reject).
        /// </summary>
        public void Invalidate()
        {
            _shapeIndex = -1;
        }

        /// <summary>
        /// Returns true exactly once per engage/release of the magnet — handy for
        /// edge-triggered logging or audio cues.
        /// </summary>
        public bool ConsumeMagnetTransition(ref bool lastMagnet)
        {
            if (_magnet == lastMagnet) return false;
            lastMagnet = _magnet;
            return true;
        }
    }
}
