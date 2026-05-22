using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.AiDriver.Telemetry
{
    /// <summary>
    /// Persists race records as pretty-printed JSON under
    /// <c>results/_telemetry/races/race_{pid}_{utc}_{guid}.json</c> and keeps
    /// at most <see cref="MaxKept"/> newest files (by mtime). Atomic write
    /// via <c>.tmp</c> + <c>File.Move</c> so a crash mid-write can never
    /// leave the dashboard reading half a file.
    ///
    /// Reuses the same project-root walk shape as
    /// <see cref="Training.TrainingTelemetry"/> so all training telemetry
    /// lands under one parent directory.
    /// </summary>
    public sealed class DiskJsonRaceSink : IRaceTelemetrySink, IRaceHistoryStore
    {
        public const int DefaultMaxKept = 50;

        // Files younger than this are NEVER pruned regardless of MaxKept.
        // Gives the Python dashboard 10 min to keep a race-detail view
        // open after the file landed on disk, so the user can navigate
        // back into a recent race without it vanishing mid-investigation.
        public const double DefaultMinAgeBeforePruneSeconds = 600.0;

        // Sidecar file written by the Python dashboard listing currently-
        // viewed race_ids (one per line, "N" GUID format). Any race file
        // whose embedded race_id appears here is exempt from prune until
        // the dashboard stops pinning it. Missing/unreadable file = no
        // pins, prune normally.
        private const string PinnedFileName = ".pinned";

        private readonly int _maxKept;
        private readonly double _minAgeBeforePruneSeconds;
        private readonly Lazy<string> _racesDir;

        public DiskJsonRaceSink(int maxKept = DefaultMaxKept,
            double minAgeBeforePruneSeconds = DefaultMinAgeBeforePruneSeconds)
        {
            if (maxKept <= 0) throw new ArgumentOutOfRangeException(nameof(maxKept));
            _maxKept = maxKept;
            _minAgeBeforePruneSeconds = Math.Max(0.0, minAgeBeforePruneSeconds);
            _racesDir = new Lazy<string>(ResolveDir);
        }

        // Test-only constructor: pin the directory rather than walking the
        // project tree. Lets the unit tests assert prune behavior in a
        // sandbox under <c>Application.temporaryCachePath</c> without
        // polluting the real <c>results/_telemetry/races/</c> dir.
        public DiskJsonRaceSink(string directoryOverride, int maxKept = DefaultMaxKept,
            double minAgeBeforePruneSeconds = 0.0)
        {
            if (maxKept <= 0) throw new ArgumentOutOfRangeException(nameof(maxKept));
            if (string.IsNullOrEmpty(directoryOverride))
                throw new ArgumentNullException(nameof(directoryOverride));
            _maxKept = maxKept;
            // Default 0 in tests so existing prune-behavior assertions keep
            // working without race-condition tolerance windows.
            _minAgeBeforePruneSeconds = Math.Max(0.0, minAgeBeforePruneSeconds);
            _racesDir = new Lazy<string>(() => directoryOverride);
        }

        public int MaxKept => _maxKept;

        public string RacesDirectory => _racesDir.Value;

        public void WriteRace(RaceRecordDto record)
        {
            if (record == null) return;
            string dir = _racesDir.Value;
            if (string.IsNullOrEmpty(dir)) return;
            try
            {
                Directory.CreateDirectory(dir);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DiskJsonRaceSink] CreateDir failed: {e.Message}");
                return;
            }

            int pid = SafePid();
            string stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            string guid = (string.IsNullOrEmpty(record.race_id)
                ? Guid.NewGuid().ToString("N").Substring(0, 8)
                : record.race_id.Replace('-', '_'));
            string fileName = $"race_{pid}_{stamp}_{guid}.json";
            string finalPath = Path.Combine(dir, fileName);
            string tmpPath = finalPath + ".tmp";

            try
            {
                // Pretty-print so a human (or a Python reader scrolling the
                // file by hand) can audit the schema without a parser.
                string json = JsonUtility.ToJson(record, prettyPrint: true);
                File.WriteAllText(tmpPath, json);
                if (File.Exists(finalPath)) File.Delete(finalPath);
                File.Move(tmpPath, finalPath);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DiskJsonRaceSink] Write failed: {e.Message}");
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                return;
            }

            try { PruneOldest(dir); } catch (Exception e)
            { Debug.LogWarning($"[DiskJsonRaceSink] Prune failed: {e.Message}"); }
        }

        public IReadOnlyList<RaceSummaryDto> List()
        {
            var result = new List<RaceSummaryDto>(_maxKept);
            string dir = _racesDir.Value;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return result;

            var files = SortedRaceFilesNewestFirst(dir);
            for (int i = 0; i < files.Count && i < _maxKept; i++)
            {
                var rec = TryReadRecord(files[i]);
                if (rec != null) result.Add(RaceSummaryDto.From(rec));
            }
            return result;
        }

        public RaceRecordDto Load(string raceId)
        {
            if (string.IsNullOrEmpty(raceId)) return null;
            string dir = _racesDir.Value;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;

            foreach (var path in SortedRaceFilesNewestFirst(dir))
            {
                var rec = TryReadRecord(path);
                if (rec != null && rec.race_id == raceId) return rec;
            }
            return null;
        }

        private static RaceRecordDto TryReadRecord(string path)
        {
            try
            {
                string json = File.ReadAllText(path);
                var rec = JsonUtility.FromJson<RaceRecordDto>(json);
                return rec;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DiskJsonRaceSink] Read failed for {path}: {e.Message}");
                return null;
            }
        }

        private void PruneOldest(string dir)
        {
            var files = SortedRaceFilesNewestFirst(dir);
            // Newest _maxKept are always kept. Beyond that, a file is only
            // pruned if (a) it has aged past the min-age floor AND (b) its
            // race_id is not in the dashboard's pin sidecar.
            HashSet<string> pins = ReadPinSet(dir);
            DateTime now = DateTime.UtcNow;
            for (int i = _maxKept; i < files.Count; i++)
            {
                string path = files[i];
                try
                {
                    if (_minAgeBeforePruneSeconds > 0.0)
                    {
                        double ageSec = (now - File.GetLastWriteTimeUtc(path)).TotalSeconds;
                        if (ageSec < _minAgeBeforePruneSeconds) continue;
                    }
                    if (pins.Count > 0)
                    {
                        string raceId = ExtractRaceIdFromFilename(path);
                        if (raceId != null && pins.Contains(raceId)) continue;
                    }
                    File.Delete(path);
                }
                catch { }
            }
        }

        private static HashSet<string> ReadPinSet(string dir)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string path = Path.Combine(dir, PinnedFileName);
            if (!File.Exists(path)) return set;
            try
            {
                foreach (var line in File.ReadAllLines(path))
                {
                    string id = line.Trim();
                    if (!string.IsNullOrEmpty(id)) set.Add(id);
                }
            }
            catch { }
            return set;
        }

        // race_<pid>_<stamp>_<race_id>.json — the GUID-N race_id is the last
        // underscore-separated segment of the stem.
        private static string ExtractRaceIdFromFilename(string path)
        {
            try
            {
                string stem = Path.GetFileNameWithoutExtension(path);
                if (string.IsNullOrEmpty(stem)) return null;
                int last = stem.LastIndexOf('_');
                if (last < 0 || last + 1 >= stem.Length) return null;
                return stem.Substring(last + 1);
            }
            catch { return null; }
        }

        private static List<string> SortedRaceFilesNewestFirst(string dir)
        {
            var list = new List<string>();
            try
            {
                foreach (var p in Directory.EnumerateFiles(dir, "race_*.json"))
                    list.Add(p);
            }
            catch { return list; }

            list.Sort((a, b) =>
            {
                DateTime ta = SafeWriteTime(a);
                DateTime tb = SafeWriteTime(b);
                return tb.CompareTo(ta);
            });
            return list;
        }

        private static DateTime SafeWriteTime(string path)
        {
            try { return File.GetLastWriteTimeUtc(path); }
            catch { return DateTime.MinValue; }
        }

        private static int SafePid()
        {
            try { return System.Diagnostics.Process.GetCurrentProcess().Id; }
            catch { return 0; }
        }

        private static string ResolveDir()
        {
            string root = TryFindProjectRoot();
            if (string.IsNullOrEmpty(root)) return null;
            return Path.Combine(root, "results", "_telemetry", "races");
        }

        // Mirror of Training.TrainingTelemetry.TryFindProjectRoot — kept
        // separate so this sink doesn't take a hard dependency on the
        // training assembly's static state (which would break the in-game
        // / player-build wiring).
        private static string TryFindProjectRoot()
        {
            try
            {
                string dataPath = Application.dataPath;
                var dir = new DirectoryInfo(dataPath);
                while (dir != null)
                {
                    if (Directory.Exists(Path.Combine(dir.FullName, "Assets")) &&
                        Directory.Exists(Path.Combine(dir.FullName, "Packages")))
                        return dir.FullName;
                    dir = dir.Parent;
                }
                var env = Environment.GetEnvironmentVariable("RACING_REPO_ROOT");
                if (!string.IsNullOrEmpty(env) && Directory.Exists(env)) return env;
            }
            catch { }
            return null;
        }
    }
}
