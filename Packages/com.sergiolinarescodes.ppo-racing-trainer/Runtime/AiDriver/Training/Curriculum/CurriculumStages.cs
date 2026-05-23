namespace UnityPpoRacingTrainer.Core.AiDriver.Training.Curriculum
{
    /// <summary>
    /// Single procedural-loop generation config used by the trainer + every
    /// inference scenario. Every closed-loop training run replays from the
    /// authored-closure library — the production circuits that ship inside the
    /// game — so the policy never sees a procedurally-recipe loop it would
    /// not encounter at play time. Authored files are rescanned on every
    /// Generate call so newly authored loops are picked up live without
    /// restarting training.
    /// </summary>
    public static class CurriculumStages
    {
        public const string AuthoredLibraryDir = "circuits/authored_closure";

        /// <summary>
        /// Canonical generation knob shared by every consumer. Piece-weight
        /// fields are inert under library replay but remain on the record so
        /// the ShapeBased generator unit tests + procedural-loop-generation
        /// scenario can still exercise the recipe path.
        /// </summary>
        public static readonly CurriculumStage Default = new(
            Id: 0,
            Name: "AuthoredPackSelfPlay",
            StraightWeight: 0.50f,
            RightCurveWeight: 0.25f,
            LeftCurveWeight: 0.25f,
            RampWeight: 0f,
            MinPieces: 28,
            MaxPieces: 60,
            MinPiecesBeforeClose: 18,
            MaxBacktracks: 16,
            TargetLapTimeSeconds: 32f);

        /// <summary>Authored-closure library dir.</summary>
        public static string LibraryDir => AuthoredLibraryDir;
    }
}
