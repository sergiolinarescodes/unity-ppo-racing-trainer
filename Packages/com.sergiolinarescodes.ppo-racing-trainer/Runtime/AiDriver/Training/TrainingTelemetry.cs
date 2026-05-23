using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.AiDriver.Training
{
    /// <summary>
    /// Append-only JSONL telemetry sink for training analytics. Each Unity
    /// process opens its own file under <c>results/_telemetry/</c> so multiple
    /// parallel envs (one per ML-Agents worker) never contend on the same
    /// stream. The python web dashboard scans the directory and merges all
    /// files for the live training visualisation.
    /// </summary>
    /// <summary>Cross-service shared state — last circuit id picked by the
    /// curriculum selector so EpisodeRunner can stamp it onto end events.</summary>
    public static class TrainingTelemetryContext
    {
        public static volatile string LastCircuitId = "";
    }

    public static class TrainingTelemetry
    {
        // Per-process rotation policy. JSONL is append-only and was
        // previously unbounded → multi-hour unattended runs filled disk
        // (and on Windows the page-file with it) → host hang. Even with
        // EpisodeRunner.StepSampleInterval cranked up, lap/wall/episode
        // events still accumulate forever without rotation. 32 MB / file,
        // 2 files / pid → ≤ 64 MB on disk per env regardless of run length.
        private const long RotateBytes = 32L * 1024L * 1024L;
        private const int KeepFilesPerPid = 2;

        private static StreamWriter _writer;
        private static readonly object _lock = new();
        private static string _filePath;
        private static volatile bool _disabledThisProcess;
        private static long _writtenBytes;
        private static int _stepSampleCounter;

        public static string FilePath => _filePath;

        /// <summary>Initialise once per process. Idempotent.</summary>
        public static void EnsureOpen()
        {
            if (_writer != null || _disabledThisProcess) return;
            lock (_lock)
            {
                if (_writer != null || _disabledThisProcess) return;
                try
                {
                    var projectRoot = TryFindProjectRoot();
                    if (projectRoot == null) { _disabledThisProcess = true; return; }
                    var dir = Path.Combine(projectRoot, "results", "_telemetry");
                    Directory.CreateDirectory(dir);
                    int pid;
                    try { pid = System.Diagnostics.Process.GetCurrentProcess().Id; }
                    catch { pid = 0; }
                    // Prune older files for THIS pid before opening — protects
                    // the rare same-pid relaunch (e.g. Editor Play/Stop loop).
                    TryPruneOldFilesForPid(dir, pid);
                    OpenFreshFile(dir, pid);
                    Emit($"{{\"event\":\"session_start\",\"pid\":{pid},\"ts\":\"{DateTime.UtcNow:o}\"}}");
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[TrainingTelemetry] disabled — open failed: {e.Message}");
                    _disabledThisProcess = true;
                }
            }
        }

        private static void OpenFreshFile(string dir, int pid)
        {
            string stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            _filePath = Path.Combine(dir, $"env_{pid}_{stamp}.jsonl");
            _writer = new StreamWriter(new FileStream(_filePath, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                AutoFlush = true
            };
            _writtenBytes = 0;
        }

        // Caller already holds _lock.
        private static void RotateIfFullLocked()
        {
            if (_writer == null || _writtenBytes < RotateBytes) return;
            string dir;
            int pid;
            try
            {
                dir = Path.GetDirectoryName(_filePath);
                if (string.IsNullOrEmpty(dir)) return;
                try { pid = System.Diagnostics.Process.GetCurrentProcess().Id; }
                catch { pid = 0; }
                _writer.Flush();
                _writer.Dispose();
                _writer = null;
                OpenFreshFile(dir, pid);
                TryPruneOldFilesForPid(dir, pid);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TrainingTelemetry] rotate failed: {e.Message}");
                _disabledThisProcess = true;
            }
        }

        private static void TryPruneOldFilesForPid(string dir, int pid)
        {
            try
            {
                var files = new List<FileInfo>();
                foreach (var p in Directory.EnumerateFiles(dir, $"env_{pid}_*.jsonl"))
                    files.Add(new FileInfo(p));
                if (files.Count <= KeepFilesPerPid) return;
                files.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                for (int i = KeepFilesPerPid; i < files.Count; i++)
                {
                    try { files[i].Delete(); } catch { }
                }
            }
            catch { }
        }

        /// <summary>Write one raw JSON line (no terminator needed). Thread-safe.</summary>
        public static void Emit(string jsonLine)
        {
            if (_disabledThisProcess) return;
            EnsureOpen();
            if (_writer == null) return;
            try
            {
                lock (_lock)
                {
                    _writer.WriteLine(jsonLine);
                    // +1 for the newline. UTF-8 byte count of ASCII-ish JSON
                    // matches char count closely enough for rotation
                    // bookkeeping — overshoot by a few percent is fine.
                    _writtenBytes += jsonLine.Length + 1;
                    RotateIfFullLocked();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TrainingTelemetry] write failed: {e.Message}");
                _disabledThisProcess = true;
            }
        }

        public static void EmitEpisodeEnd(
            int carIdHash,
            string circuit,
            string endReason,
            float endX,
            float endZ,
            float lapFraction,
            int lapsCompleted,
            int steps,
            float elapsedSec,
            float cumulativeReward,
            int wallHitCount,
            float health = 1f)
        {
            var sb = new StringBuilder(256);
            sb.Append("{\"event\":\"episode_end\"");
            sb.Append(",\"ts\":\""); sb.Append(DateTime.UtcNow.ToString("o")); sb.Append('\"');
            sb.Append(",\"car\":"); sb.Append(carIdHash);
            sb.Append(",\"circuit\":"); AppendStringJson(sb, circuit ?? "");
            sb.Append(",\"reason\":"); AppendStringJson(sb, endReason ?? "");
            sb.Append(",\"x\":"); AppendFloat(sb, endX);
            sb.Append(",\"z\":"); AppendFloat(sb, endZ);
            sb.Append(",\"lap_frac\":"); AppendFloat(sb, lapFraction);
            sb.Append(",\"laps\":"); sb.Append(lapsCompleted);
            sb.Append(",\"steps\":"); sb.Append(steps);
            sb.Append(",\"elapsed\":"); AppendFloat(sb, elapsedSec);
            sb.Append(",\"reward\":"); AppendFloat(sb, cumulativeReward);
            sb.Append(",\"wall_hits\":"); sb.Append(wallHitCount);
            // Chassis-damage telemetry — enables damage curve and clean-lap
            // rate plots in the dashboard. damage = 1 - health, in [0, 1].
            sb.Append(",\"health\":"); AppendFloat(sb, health);
            sb.Append(",\"damage\":"); AppendFloat(sb, 1f - health);
            sb.Append('}');
            Emit(sb.ToString());
        }

        public static void EmitStepSample(
            int carIdHash,
            string circuit,
            float x,
            float z,
            float lapFraction,
            float speed)
        {
            var sb = new StringBuilder(160);
            sb.Append("{\"event\":\"step_sample\"");
            sb.Append(",\"car\":"); sb.Append(carIdHash);
            sb.Append(",\"circuit\":"); AppendStringJson(sb, circuit ?? "");
            sb.Append(",\"x\":"); AppendFloat(sb, x);
            sb.Append(",\"z\":"); AppendFloat(sb, z);
            sb.Append(",\"lap_frac\":"); AppendFloat(sb, lapFraction);
            sb.Append(",\"speed\":"); AppendFloat(sb, speed);
            sb.Append('}');
            Emit(sb.ToString());
        }

        public static void EmitWallHit(
            int carIdHash,
            string circuit,
            float x,
            float z)
        {
            var sb = new StringBuilder(140);
            sb.Append("{\"event\":\"wall_hit\"");
            sb.Append(",\"car\":"); sb.Append(carIdHash);
            sb.Append(",\"circuit\":"); AppendStringJson(sb, circuit ?? "");
            sb.Append(",\"x\":"); AppendFloat(sb, x);
            sb.Append(",\"z\":"); AppendFloat(sb, z);
            sb.Append('}');
            Emit(sb.ToString());
        }

        /// <summary>One JSONL line per micro-sector boundary crossed in lap order.
        /// Aggregated server-side by (car, circuit, lap) → sector splits per lap.</summary>
        public static void EmitMicroSector(
            int carIdHash,
            string circuit,
            int lap,
            int sector,
            float tLap)
        {
            var sb = new StringBuilder(160);
            sb.Append("{\"event\":\"micro_sector\"");
            sb.Append(",\"car\":"); sb.Append(carIdHash);
            sb.Append(",\"circuit\":"); AppendStringJson(sb, circuit ?? "");
            sb.Append(",\"lap\":"); sb.Append(lap);
            sb.Append(",\"sector\":"); sb.Append(sector);
            sb.Append(",\"t\":"); AppendFloat(sb, tLap);
            sb.Append('}');
            Emit(sb.ToString());
        }

        /// <summary>One JSONL line per lap completion in multi-lap mode.
        /// Lets the dashboard show live lap rows without waiting for the
        /// (~5-min) episode-end. Server's lap-log + per-circuit stats
        /// recognise event="lap_complete" the same way as a Success episode_end.</summary>
        public static void EmitLap(
            int carIdHash,
            string circuit,
            int lap,
            float lapSec,
            int lapSteps,
            float lapReward,
            int wallHitsThisLap,
            bool isFlying,
            float health = 1f)
        {
            var sb = new StringBuilder(240);
            sb.Append("{\"event\":\"lap_complete\"");
            sb.Append(",\"ts\":\""); sb.Append(DateTime.UtcNow.ToString("o")); sb.Append('\"');
            sb.Append(",\"car\":"); sb.Append(carIdHash);
            sb.Append(",\"circuit\":"); AppendStringJson(sb, circuit ?? "");
            sb.Append(",\"lap\":"); sb.Append(lap);
            // Lap-kind tag. Cold lap = lap 1 of the episode (agent
            // accelerating from spawn pose, slower by physics not skill).
            // Flying lap = lap >= 2 (agent crossed the start at race speed
            // from the prior lap). The dashboard splits leaderboards by
            // kind so "best flying lap" is the real-pace number; "best
            // cold lap" is mostly a curiosity / launch-control gauge.
            sb.Append(",\"kind\":\""); sb.Append(isFlying ? "flying" : "cold"); sb.Append('\"');
            sb.Append(",\"flying\":"); sb.Append(isFlying ? "true" : "false");
            sb.Append(",\"seconds\":"); AppendFloat(sb, lapSec);
            sb.Append(",\"steps\":"); sb.Append(lapSteps);
            sb.Append(",\"reward\":"); AppendFloat(sb, lapReward);
            sb.Append(",\"wall_hits\":"); sb.Append(wallHitsThisLap);
            sb.Append(",\"health\":"); AppendFloat(sb, health);
            sb.Append(",\"damage\":"); AppendFloat(sb, 1f - health);
            sb.Append('}');
            Emit(sb.ToString());
        }

        public static void EmitCircuitChange(string circuit, int pieces, float length)
        {
            var sb = new StringBuilder(160);
            sb.Append("{\"event\":\"circuit_change\"");
            sb.Append(",\"ts\":\""); sb.Append(DateTime.UtcNow.ToString("o")); sb.Append('\"');
            sb.Append(",\"circuit\":"); AppendStringJson(sb, circuit ?? "");
            sb.Append(",\"pieces\":"); sb.Append(pieces);
            sb.Append(",\"length\":"); AppendFloat(sb, length);
            sb.Append('}');
            Emit(sb.ToString());
        }

        private static void AppendFloat(StringBuilder sb, float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) { sb.Append("null"); return; }
            sb.Append(v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        }

        private static void AppendStringJson(StringBuilder sb, string s)
        {
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\' || c == '"') { sb.Append('\\').Append(c); }
                else if (c == '\n') { sb.Append("\\n"); }
                else if (c == '\r') { sb.Append("\\r"); }
                else if (c == '\t') { sb.Append("\\t"); }
                else if (c < 32) { sb.AppendFormat("\\u{0:X4}", (int)c); }
                else { sb.Append(c); }
            }
            sb.Append('"');
        }

        private static string TryFindProjectRoot()
        {
            try
            {
                // Application.dataPath in player builds is <build>/<exe>_Data
                // Project root is two-up from there OR project root if Editor.
                string dataPath = Application.dataPath;
                var dir = new DirectoryInfo(dataPath);
                while (dir != null)
                {
                    if (Directory.Exists(Path.Combine(dir.FullName, "Assets")) &&
                        Directory.Exists(Path.Combine(dir.FullName, "Packages")))
                        return dir.FullName;
                    dir = dir.Parent;
                }
                // Player build: walk up from <build>/<exe>_Data → <build> → use repo path env if set
                var env = Environment.GetEnvironmentVariable("RACING_REPO_ROOT");
                if (!string.IsNullOrEmpty(env) && Directory.Exists(env)) return env;
            }
            catch { }
            return null;
        }

        internal static string TryFindProjectRootPublic() => TryFindProjectRoot();
    }

    /// <summary>
    /// Permanent per-circuit fastest-lap record store. Lives in
    /// <c>tools/circuit_records/records.json</c> — outside the rotating
    /// telemetry path so historical bests survive every run, supervisor
    /// restart, and results/ wipe.
    ///
    /// Both the C# trainer (writer when a new flying lap beats the stored
    /// best) and the Python tierlist server (writer when the aggregator
    /// processes flying laps) call into this same file. Writes are atomic
    /// (tmp + replace) and convergent under min-merge — rare race losses
    /// self-heal on the next write.
    ///
    /// The reward shaper reads this to feed lap-time targets back into PPO
    /// (see RewardShaper.OnCircuitBestLapKnown).
    /// </summary>
    public static class CircuitRecordsStore
    {
        private const string SubDir = "tools/circuit_records";
        private const string FileName = "records.json";
        private const float ReadCacheSec = 10f;

        private static readonly object _lock = new();
        private static readonly Dictionary<string, float> _cache = new();
        private static float _cacheLoadedAt = -1f;
        private static string _resolvedPath;

        public static string FilePath
        {
            get
            {
                if (_resolvedPath != null) return _resolvedPath;
                var root = TrainingTelemetry.TryFindProjectRootPublic();
                if (string.IsNullOrEmpty(root)) return null;
                _resolvedPath = Path.Combine(root, SubDir, FileName);
                return _resolvedPath;
            }
        }

        /// <summary>Read-side: returns best lap seconds for circuitId, or 0 if unknown.</summary>
        public static float TryGetBestLap(string circuitId)
        {
            if (string.IsNullOrEmpty(circuitId)) return 0f;
            lock (_lock)
            {
                if (Time.realtimeSinceStartup - _cacheLoadedAt > ReadCacheSec)
                    ReloadCacheLocked();
                return _cache.TryGetValue(circuitId, out var v) ? v : 0f;
            }
        }

        /// <summary>Write-side: merge-min update. Returns true if a new record was set.</summary>
        public static bool TryUpsertBestLap(string circuitId, float lapSeconds, string runId)
        {
            if (string.IsNullOrEmpty(circuitId) || lapSeconds <= 0f || !float.IsFinite(lapSeconds)) return false;
            string path = FilePath;
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                lock (_lock)
                {
                    // Read existing on disk (NOT cache — cache might be stale vs python writes).
                    var existing = ReadFromDisk(path);
                    if (existing.TryGetValue(circuitId, out var prev) && prev > 0f && lapSeconds >= prev) return false;
                    existing[circuitId] = lapSeconds;
                    WriteToDisk(path, existing, circuitId, lapSeconds, runId);
                    // Refresh cache so the next reader sees the new best.
                    _cache.Clear();
                    foreach (var kv in existing) _cache[kv.Key] = kv.Value;
                    _cacheLoadedAt = Time.realtimeSinceStartup;
                    return true;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CircuitRecordsStore] upsert failed: {e.Message}");
                return false;
            }
        }

        private static void ReloadCacheLocked()
        {
            _cache.Clear();
            string path = FilePath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                _cacheLoadedAt = Time.realtimeSinceStartup;
                return;
            }
            try
            {
                foreach (var kv in ReadFromDisk(path)) _cache[kv.Key] = kv.Value;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CircuitRecordsStore] reload failed: {e.Message}");
            }
            _cacheLoadedAt = Time.realtimeSinceStartup;
        }

        // Bare-bones JSON parse — schema is {"version":1,"circuits":{"id":{"best_lap_seconds":N,...}}}.
        // Avoids dragging in a JSON dep for one file. Picks "best_lap_seconds" out of each
        // circuit object, ignores everything else.
        private static Dictionary<string, float> ReadFromDisk(string path)
        {
            var map = new Dictionary<string, float>();
            if (!File.Exists(path)) return map;
            string text = File.ReadAllText(path);
            int circuitsIdx = text.IndexOf("\"circuits\"", StringComparison.Ordinal);
            if (circuitsIdx < 0) return map;
            int braceStart = text.IndexOf('{', circuitsIdx);
            if (braceStart < 0) return map;
            int braceEnd = FindMatchingBrace(text, braceStart);
            if (braceEnd < 0) return map;
            int p = braceStart + 1;
            while (p < braceEnd)
            {
                int keyStart = text.IndexOf('"', p);
                if (keyStart < 0 || keyStart >= braceEnd) break;
                int keyEnd = text.IndexOf('"', keyStart + 1);
                if (keyEnd < 0 || keyEnd >= braceEnd) break;
                string key = text.Substring(keyStart + 1, keyEnd - keyStart - 1);
                int objStart = text.IndexOf('{', keyEnd);
                if (objStart < 0 || objStart >= braceEnd) break;
                int objEnd = FindMatchingBrace(text, objStart);
                if (objEnd < 0 || objEnd > braceEnd) break;
                string obj = text.Substring(objStart, objEnd - objStart + 1);
                int blsIdx = obj.IndexOf("\"best_lap_seconds\"", StringComparison.Ordinal);
                if (blsIdx >= 0)
                {
                    int colon = obj.IndexOf(':', blsIdx);
                    if (colon > 0)
                    {
                        int valEnd = colon + 1;
                        while (valEnd < obj.Length && (obj[valEnd] == ' ' || obj[valEnd] == '\t')) valEnd++;
                        int numStart = valEnd;
                        while (valEnd < obj.Length && "0123456789.-eE+".IndexOf(obj[valEnd]) >= 0) valEnd++;
                        if (valEnd > numStart && float.TryParse(obj.Substring(numStart, valEnd - numStart),
                            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f))
                            map[key] = f;
                    }
                }
                p = objEnd + 1;
            }
            return map;
        }

        private static int FindMatchingBrace(string text, int openIdx)
        {
            int depth = 0;
            for (int i = openIdx; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '{') depth++;
                else if (c == '}') { depth--; if (depth == 0) return i; }
                else if (c == '"') { i = SkipString(text, i); }
            }
            return -1;
        }

        private static int SkipString(string text, int quoteIdx)
        {
            for (int i = quoteIdx + 1; i < text.Length; i++)
            {
                if (text[i] == '\\') { i++; continue; }
                if (text[i] == '"') return i;
            }
            return text.Length - 1;
        }

        private static void WriteToDisk(string path, Dictionary<string, float> all,
            string updatedCircuit, float updatedLapSec, string runId)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var sb = new StringBuilder(256 + all.Count * 96);
            sb.Append("{\n  \"version\": 1,\n  \"circuits\": {");
            string ts = DateTime.UtcNow.ToString("o");
            int n = 0;
            foreach (var kv in all)
            {
                if (n++ > 0) sb.Append(',');
                sb.Append("\n    \"").Append(EscapeJson(kv.Key)).Append("\": {");
                sb.Append("\"best_lap_seconds\": ").Append(kv.Value.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture));
                if (kv.Key == updatedCircuit)
                {
                    sb.Append(", \"run_id\": \"").Append(EscapeJson(runId ?? "")).Append('"');
                    sb.Append(", \"timestamp_utc\": \"").Append(ts).Append('"');
                    sb.Append(", \"writer\": \"csharp\"");
                }
                sb.Append('}');
            }
            sb.Append("\n  }\n}\n");
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, sb.ToString());
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
            {
                if (c == '\\' || c == '"') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < 32) sb.AppendFormat("\\u{0:X4}", (int)c);
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Published by TrainingDirector after a circuit regen so RewardShaper
    /// can fetch the historical fastest lap for the new circuit. Separate
    /// from CircuitRegeneratedEvent to keep that event's signature stable.
    /// </summary>
    public readonly record struct CircuitBestLapKnownEvent(string CircuitId, float BestLapSeconds);
}
