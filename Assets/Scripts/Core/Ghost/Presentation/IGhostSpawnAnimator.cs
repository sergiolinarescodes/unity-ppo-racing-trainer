using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Ghost.Presentation
{
    /// <summary>
    /// Reusable drop-from-air handle. Owner ticks <see cref="Update"/> each frame;
    /// renderer reads <see cref="CurrentYOffset"/> + <see cref="CurrentAlpha"/>.
    /// Used by the ghost car, the dynamic racing-line kerb service, and the
    /// player-card placement animation — anything that wants the same drop-then-
    /// settle motion stays consistent through this interface.
    /// </summary>
    public interface IDropHandle
    {
        bool IsLanded { get; }
        bool IsSettleComplete { get; }
        float Progress { get; }
        float CurrentYOffset { get; }
        float CurrentAlpha { get; }
        void Update(float deltaTime);
    }

    /// <summary>
    /// Reusable drop-from-air animator. Returns a fresh handle per call so each
    /// dropped object can tick its own clock independently.
    /// </summary>
    public interface IDropFromAirAnimator
    {
        IDropHandle PlayDropFromAir(Vector3 landPos, float heading, float dropHeight = 6f);
    }

    /// <summary>
    /// Ghost-specific alias of <see cref="IDropHandle"/>. Kept so the existing
    /// ghost director / scenarios compile unchanged; new callers should depend on
    /// <see cref="IDropHandle"/> directly.
    /// </summary>
    public interface IGhostSpawnHandle : IDropHandle { }

    /// <summary>
    /// Ghost-specific alias of <see cref="IDropFromAirAnimator"/>. Kept so the
    /// existing ghost director / scenarios compile unchanged; new callers should
    /// depend on <see cref="IDropFromAirAnimator"/> directly.
    /// </summary>
    public interface IGhostSpawnAnimator : IDropFromAirAnimator
    {
        new IGhostSpawnHandle PlayDropFromAir(Vector3 landPos, float heading, float dropHeight = 6f);
    }
}
