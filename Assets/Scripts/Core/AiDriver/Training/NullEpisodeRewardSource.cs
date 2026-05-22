using UnityPpoRacingTrainer.Core.AiDriver.Physics;

namespace UnityPpoRacingTrainer.Core.AiDriver.Training
{
    /// <summary>
    /// Sentinel reward source for inference / heuristic eval — no shaping, no
    /// episode termination. Lets the policy service treat training as opt-in
    /// without an `if` branch on every tick.
    /// </summary>
    public sealed class NullEpisodeRewardSource : IEpisodeRewardSource
    {
        public static readonly NullEpisodeRewardSource Instance = new();

        private NullEpisodeRewardSource() { }

        public void OnEpisodeBegin(CarId carId) { }
        public StepResult PostStep(CarId carId) => StepResult.None;
        public void OnAgentUnregistered(CarId carId) { }
    }
}
