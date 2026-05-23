using UnityPpoRacingTrainer.Core.AiDriver.Policy;

namespace UnityPpoRacingTrainer.Core.Ghost.Simulation
{
    /// <summary>
    /// Canonical defaults for the single non-training ghost car. Mirrors the
    /// <c>AiDriverPhysicsDefaults</c> pattern: static accessors so callers
    /// don't reach back into a DI container, version-agnostic where possible,
    /// and the only place these literals live in the codebase.
    ///
    /// Why these values: the trainer randomises personality across a stage's
    /// sampler and starting fuel inside [80, 120] L. For a one-car observation
    /// scene we want the centre of those distributions so the policy sees its
    /// conditioning at the unbiased baseline — anything else is an arbitrary
    /// archetype that biases the demo.
    /// </summary>
    public static class GhostDriverDefaults
    {
        public static DriverPersonality Personality => DriverPersonality.AllRounder;
        public const float StartingFuelLiters = 100f;
    }
}
