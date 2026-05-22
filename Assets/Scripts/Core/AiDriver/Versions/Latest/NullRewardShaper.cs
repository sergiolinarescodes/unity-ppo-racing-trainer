using UnityPpoRacingTrainer.Core.AiDriver.Physics;
using UnityPpoRacingTrainer.Core.AiDriver.Training;

namespace UnityPpoRacingTrainer.Core.AiDriver.Versions.Latest
{
    /// <summary>
    /// No-op reward shaper used as the fallback when no real shaper is
    /// registered (e.g. <c>AIDRIVER_TRAINING</c> is off in a player/inference
    /// build, where the policy runs from a frozen ONNX and the shaper is
    /// never sampled). All methods return zero / empty <see cref="StepResult"/>.
    /// </summary>
    internal sealed class NullRewardShaper : IRewardShaper
    {
        public static readonly NullRewardShaper Instance = new();

        public void OnEpisodeBegin(CarId carId) { }
        public StepResult Drain(CarId carId) => StepResult.None;
        public StepResult AccumulatePerTick(CarId carId, float dt) => StepResult.None;
    }
}
