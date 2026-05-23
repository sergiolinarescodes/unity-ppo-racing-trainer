using System.Collections.Generic;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Track.Ribbon
{
    /// <summary>
    /// Maintains one continuous "ribbon" mesh per chain of connected pieces — the
    /// visible road surface that spans across multiple cube-aligned pieces and
    /// drapes over terrain. Rebuilds on track-piece placement / removal events.
    /// Per-piece slabs continue to render the foundation (sides + understructure);
    /// the ribbon paints the road top with cross-piece smoothing.
    /// </summary>
    public interface ITrackRibbonService
    {
        IReadOnlyList<GameObject> Ribbons { get; }
        void Rebuild();
    }
}
