namespace UnityPpoRacingTrainer.Core.AiDriver.Physics
{
    /// <summary>
    /// One frame of driver input. Steer and Throttle are clamped to [-1, 1].
    /// Boost fires when the budget is positive — the simulation enforces the budget,
    /// so callers can request boost every frame without bookkeeping.
    /// </summary>
    public readonly record struct DriverInput(float Steer, float Throttle, bool Boost);
}
