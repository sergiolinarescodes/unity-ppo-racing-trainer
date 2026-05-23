using System;

namespace UnityPpoRacingTrainer.Core.AiDriver.Telemetry
{
    /// <summary>
    /// Reservoir sampler of size 1 over a fixed-size window. Implements
    /// Algorithm R: at episode i within the window (1-indexed), the new race
    /// becomes the candidate with probability 1/i. After WindowSize episodes
    /// the candidate is the chosen sample (uniformly distributed over the
    /// window) and the recorder flushes it. The window then resets and the
    /// next 1 000 episodes vote on the next sample.
    ///
    /// Plain System.Random so the sampler is deterministic under a seed —
    /// the unit tests rely on this to assert exact-once selection across
    /// many windows.
    /// </summary>
    public sealed class ReservoirRaceSampler
    {
        private readonly Random _rng;
        private int _counter;
        private bool _windowJustClosed;

        public int WindowSize { get; }

        public ReservoirRaceSampler(int windowSize = 1000, Random rng = null)
        {
            if (windowSize <= 0) throw new ArgumentOutOfRangeException(nameof(windowSize));
            WindowSize = windowSize;
            _rng = rng ?? new Random();
        }

        /// <summary>Episodes seen in the current (open) window. 0 means the
        /// window is fresh.</summary>
        public int EpisodesInWindow => _counter;

        public bool WindowJustClosed => _windowJustClosed;

        /// <summary>
        /// Account for an ended episode. Returns <c>true</c> iff this race
        /// should replace the current candidate buffer. On the WindowSize-th
        /// call, <see cref="WindowJustClosed"/> becomes true so the caller
        /// can flush; then <see cref="ResetWindow"/> rolls over.
        /// </summary>
        public bool OnEpisodeEnded()
        {
            _counter++;
            // p = 1/_counter — uniform survival via Algorithm R for k=1.
            bool replace = _counter == 1 || _rng.Next(_counter) == 0;
            _windowJustClosed = _counter >= WindowSize;
            return replace;
        }

        public void ResetWindow()
        {
            _counter = 0;
            _windowJustClosed = false;
        }
    }
}
