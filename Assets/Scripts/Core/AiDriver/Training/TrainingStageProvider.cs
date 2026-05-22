using System;

namespace UnityPpoRacingTrainer.Core.AiDriver.Training
{
    /// <summary>
    /// Resolves the currently active ML-Agents curriculum stage id. The
    /// trainer bootstrap sets <see cref="Resolver"/> to read
    /// <c>Academy.EnvironmentParameters.GetWithDefault("stage_id", 0)</c>;
    /// non-trainer scenes leave the resolver null and the stage falls back to
    /// <see cref="TrainingStageProvider.DefaultStage"/>.
    /// </summary>
    public interface IStageIdProvider
    {
        Func<int> Resolver { get; set; }
        int Resolve();
    }

    internal sealed class TrainingStageProvider : IStageIdProvider
    {
        public const int DefaultStage = 0;

        public Func<int> Resolver { get; set; }

        public int Resolve() => Resolver?.Invoke() ?? DefaultStage;
    }
}
