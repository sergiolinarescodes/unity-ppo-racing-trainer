using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.AiDriver.Editor
{
    /// <summary>
    /// Builds the headless AI driver trainer. Two entry points:
    /// <list type="bullet">
    /// <item>Editor menu <c>Build > AI Driver Trainer (Headless)</c> — interactive.</item>
    /// <item><c>BuildTrainerCli</c> — invoke from CI / shell with:
    /// <code>
    /// Unity -batchmode -nographics -projectPath . `
    ///     -executeMethod UnityPpoRacingTrainer.Core.AiDriver.Editor.TrainerBuildPipeline.BuildTrainerCli `
    ///     -quit -logFile -
    /// </code>
    /// Process exits non-zero on failure so CI can detect a broken build.</item>
    /// </list>
    /// Output lands at <c>Build/AiDriverTrainer/</c> with the platform-native artifact
    /// (.exe on Windows, .app bundle on macOS, .x86_64 on Linux). The build forces
    /// the <c>AIDRIVER_TRAINING</c> scripting define so the trainer scene's
    /// bootstrap path compiles in.
    /// </summary>
    public static class TrainerBuildPipeline
    {
        private const string OutputDir = "Build/AiDriverTrainer";
        private const string TrainerScene = "Assets/_Bootstrap/Trainer.unity";

        [MenuItem("Build/AI Driver Trainer (Headless)")]
        public static void BuildTrainerMenu()
        {
            var report = Build();
            if (report.summary.result == BuildResult.Succeeded)
            {
                Debug.Log($"[TrainerBuildPipeline] Build OK: {report.summary.outputPath} ({report.summary.totalSize} bytes, {report.summary.totalErrors} errors)");
            }
            else
            {
                Debug.LogError($"[TrainerBuildPipeline] Build failed: {report.summary.result} ({report.summary.totalErrors} errors)");
            }
        }

        public static void BuildTrainerCli()
        {
            var report = Build();
            int exitCode = report.summary.result == BuildResult.Succeeded ? 0 : 1;
            EditorApplication.Exit(exitCode);
        }

        private static BuildReport Build()
        {
            Directory.CreateDirectory(OutputDir);
            ResolveHostTarget(out BuildTarget target, out string outputName);
            var options = new BuildPlayerOptions
            {
                scenes = new[] { TrainerScene },
                locationPathName = Path.Combine(OutputDir, outputName),
                target = target,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.None,
                extraScriptingDefines = new[] { "AIDRIVER_TRAINING" },
            };
            return BuildPipeline.BuildPlayer(options);
        }

        private static void ResolveHostTarget(out BuildTarget target, out string outputName)
        {
            switch (Application.platform)
            {
                case RuntimePlatform.OSXEditor:
                    target = BuildTarget.StandaloneOSX;
                    outputName = "AiDriverTrainer.app";
                    break;
                case RuntimePlatform.LinuxEditor:
                    target = BuildTarget.StandaloneLinux64;
                    outputName = "AiDriverTrainer.x86_64";
                    break;
                default:
                    target = BuildTarget.StandaloneWindows64;
                    outputName = "AiDriverTrainer.exe";
                    break;
            }
        }
    }
}
