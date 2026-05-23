using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Ghost.Presentation
{
    /// <summary>
    /// Visual-only adapter for the ghost car. Accepts a pose every render frame
    /// and updates a translucent cube + wheel mesh children. Never reads from
    /// the simulation layer — pose values arrive precomputed from the director.
    /// </summary>
    public interface IGhostCarPresenter
    {
        void Show(Vector3 worldPos, float headingRad, float bodyLeanRad, float alpha = 1f);
        void Hide();
        Transform Root { get; }
    }
}
