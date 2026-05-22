using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Ghost.Presentation
{
    /// <summary>
    /// Concrete drop-from-air animator. Implements both <see cref="IGhostSpawnAnimator"/>
    /// (ghost-specific alias) and <see cref="IDropFromAirAnimator"/> (shared) so the
    /// existing director + scenarios keep working while new clients (placement-anim,
    /// dynamic-kerb service) can resolve the shared interface and get an
    /// <see cref="IDropHandle"/>.
    /// </summary>
    internal sealed class GhostSpawnAnimator : IGhostSpawnAnimator
    {
        // Total drop + settle time. Drop = ground-touch; settle = post-bounce.
        private const float DropSeconds = 0.45f;
        private const float SettleSeconds = 0.20f;
        private const float FadeInSeconds = 0.30f;

        public IGhostSpawnHandle PlayDropFromAir(Vector3 landPos, float heading, float dropHeight = 6f)
            => new Handle(landPos, heading, dropHeight);

        IDropHandle IDropFromAirAnimator.PlayDropFromAir(Vector3 landPos, float heading, float dropHeight)
            => new Handle(landPos, heading, dropHeight);

        private sealed class Handle : IGhostSpawnHandle
        {
            private readonly Vector3 _landPos;
            private readonly float _heading;
            private readonly float _dropHeight;
            private float _elapsed;

            public Handle(Vector3 landPos, float heading, float dropHeight)
            {
                _landPos = landPos;
                _heading = heading;
                _dropHeight = dropHeight;
            }

            public bool IsLanded => _elapsed >= DropSeconds;
            public bool IsSettleComplete => _elapsed >= DropSeconds + SettleSeconds;
            public float Progress => Mathf.Clamp01(_elapsed / (DropSeconds + SettleSeconds));

            public float CurrentYOffset
            {
                get
                {
                    if (_elapsed < DropSeconds)
                    {
                        // Cubic ease-in for the drop — slow at start, fast at impact.
                        float t = _elapsed / DropSeconds;
                        float eased = 1f - (1f - t) * (1f - t) * (1f - t);
                        return _dropHeight * (1f - eased);
                    }
                    // Post-land: hold at ground. The previous half-sine bounce
                    // read as a second drop animation and confused the player —
                    // a single landing motion is preferable to a re-rise.
                    return 0f;
                }
            }

            public float CurrentAlpha
            {
                get
                {
                    if (_elapsed >= FadeInSeconds) return 1f;
                    return _elapsed / FadeInSeconds;
                }
            }

            public void Update(float deltaTime)
            {
                _elapsed = Mathf.Max(0f, _elapsed + deltaTime);
            }
        }
    }
}
