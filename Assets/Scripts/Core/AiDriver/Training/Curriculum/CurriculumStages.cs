using System.Collections.Generic;

namespace UnityPpoRacingTrainer.Core.AiDriver.Training.Curriculum
{
    /// <summary>
    /// Curriculum ladder used by the trainer to dial difficulty across an
    /// unattended PPO run. EVERY stage now replays from the authored-closure
    /// library — the production circuits that ship inside the game — so the
    /// policy never sees a procedurally-recipe loop it would not encounter at
    /// play time. Stage-id is still consumed by the reward shaper for feature
    /// gating (fuel / tire / opponents / personality archetype), but the
    /// circuit pool is identical across stages. Authored files are rescanned
    /// on every Generate call so newly authored loops are picked up live
    /// without restarting training.
    /// </summary>
    public static class CurriculumStages
    {
        public const int AuthoredStageId = 4;
        public const string AuthoredLibraryDir = "circuits/stage_authored_closure";

        private static readonly CurriculumStage[] _all =
        {
            // Stage 0 retains the original Warmup recipe-friendly piece counts
            // so the ShapeBased generator can still be exercised by unit tests
            // and the procedural-loop-generation scenario; in training the
            // routing is uniform across stages (all → authored library), so
            // these numbers are inert for the trainer.
            new(Id: 0, Name: "AuthoredSolo",
                StraightWeight: 0.95f, RightCurveWeight: 0.025f,
                LeftCurveWeight: 0.025f, RampWeight: 0f,
                MinPieces: 8, MaxPieces: 16,
                MinPiecesBeforeClose: 6, MaxBacktracks: 4,
                TargetLapTimeSeconds: 10f),

            new(Id: 1, Name: "AuthoredTire",
                StraightWeight: 0.50f, RightCurveWeight: 0.25f,
                LeftCurveWeight: 0.25f, RampWeight: 0f,
                MinPieces: 28, MaxPieces: 60,
                MinPiecesBeforeClose: 18, MaxBacktracks: 16,
                TargetLapTimeSeconds: 32f),

            new(Id: 2, Name: "AuthoredFuel",
                StraightWeight: 0.50f, RightCurveWeight: 0.25f,
                LeftCurveWeight: 0.25f, RampWeight: 0f,
                MinPieces: 28, MaxPieces: 60,
                MinPiecesBeforeClose: 18, MaxBacktracks: 16,
                TargetLapTimeSeconds: 32f),

            new(Id: 3, Name: "AuthoredTireFuel",
                StraightWeight: 0.50f, RightCurveWeight: 0.25f,
                LeftCurveWeight: 0.25f, RampWeight: 0f,
                MinPieces: 28, MaxPieces: 60,
                MinPiecesBeforeClose: 18, MaxBacktracks: 16,
                TargetLapTimeSeconds: 32f),

            new(Id: AuthoredStageId, Name: "AuthoredTwoCar",
                StraightWeight: 0.50f, RightCurveWeight: 0.25f,
                LeftCurveWeight: 0.25f, RampWeight: 0f,
                MinPieces: 28, MaxPieces: 60,
                MinPiecesBeforeClose: 18, MaxBacktracks: 16,
                TargetLapTimeSeconds: 32f),

            new(Id: 5, Name: "AuthoredPackSelfPlay",
                StraightWeight: 0.50f, RightCurveWeight: 0.25f,
                LeftCurveWeight: 0.25f, RampWeight: 0f,
                MinPieces: 28, MaxPieces: 60,
                MinPiecesBeforeClose: 18, MaxBacktracks: 16,
                TargetLapTimeSeconds: 32f),
        };

        public static IReadOnlyList<CurriculumStage> All => _all;

        public static bool TryGet(int id, out CurriculumStage stage)
        {
            for (int i = 0; i < _all.Length; i++)
            {
                if (_all[i].Id == id)
                {
                    stage = _all[i];
                    return true;
                }
            }
            stage = default;
            return false;
        }

        /// <summary>
        /// Authored-closure library dir for every stage. Procedural recipe
        /// generation was retired so the policy only ever trains on circuits
        /// that ship inside the game.
        /// </summary>
        public static string LibraryDirFor(int stageId) => AuthoredLibraryDir;

        /// <summary>True for every stage — all training is library-driven now.</summary>
        public static bool IsLibraryStage(int stageId) => true;
    }
}
