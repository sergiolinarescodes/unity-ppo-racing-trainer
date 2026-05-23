using System;
using System.Collections.Generic;
using System.IO;
using UnityPpoRacingTrainer.Core.AiDriver.Training.Curriculum;
using UnityPpoRacingTrainer.Core.Track;
using UnityPpoRacingTrainer.Core.Track.Loop;
using Unidad.Core.Grid;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.AiDriver.Training.Generation
{
    /// <summary>
    /// Routes <see cref="IProceduralLoopGenerator.Generate"/> to a random
    /// authored-closure circuit on every episode. <see cref="ShapeBasedLoopGenerator"/>
    /// is retained ONLY as a crash-time fallback for the empty-library case
    /// (it should never fire in production — the authored corpus has ~92
    /// circuits).
    /// </summary>
    internal sealed class CurriculumGeneratorSelector : IProceduralLoopGenerator
    {
        private readonly ShapeBasedLoopGenerator _shapeBased;
        private readonly ITrackPlacementService _placement;
        private readonly IClosedLoopService _loop;
        private readonly System.Random _rng = new();

        /// <summary>
        /// When set to a non-empty 8-char circuit id (e.g. "08c66d8e"), the
        /// selector loads ONLY that circuit from the library and replays it
        /// on every Generate call. Empty = random pick. Set by
        /// <c>TrainerBootstrap.forceCircuitId</c> for editor inference.
        /// </summary>
        public string ForcedCircuitId { get; set; }

        public CurriculumGeneratorSelector(
            ShapeBasedLoopGenerator shapeBased,
            ITrackPlacementService placement,
            IClosedLoopService loop)
        {
            _shapeBased = shapeBased;
            _placement = placement;
            _loop = loop;
        }

        public GenerationResult Generate(in GenerationConfig cfg)
        {
            // Forced-id wins over library routing: if a specific circuit is
            // pinned (editor inference / debugging), replay it directly. The
            // caller is responsible for clearing the field before resuming
            // production training.
            string forced = ForcedCircuitId;
            if (!string.IsNullOrEmpty(forced))
            {
                string forcedPath = FindForcedCircuit(forced);
                if (forcedPath != null)
                {
                    var result = TryReplayPath(forcedPath);
                    if (result.Success) return result;
                    Debug.LogWarning($"[CurriculumSelector] Forced circuit '{forced}' failed to replay; falling back to library routing.");
                }
                else
                {
                    Debug.LogWarning($"[CurriculumSelector] Forced circuit id '{forced}' not found in the library; falling back to library routing.");
                }
            }

            return TryReplayFromLibrary(CurriculumStages.LibraryDir, cfg);
        }

        private GenerationResult TryReplayFromLibrary(string dir, in GenerationConfig cfg)
        {
            var files = GetFiles(dir);
            if (files.Count == 0)
            {
                Debug.LogWarning($"[CurriculumSelector] Library '{dir}' empty; falling back to ShapeBased.");
                return _shapeBased.Generate(cfg);
            }

            for (int attempt = 0; attempt < 4; attempt++)
            {
                var path = files[_rng.Next(files.Count)];
                CircuitFile rec;
                try
                {
                    rec = JsonUtility.FromJson<CircuitFile>(File.ReadAllText(path));
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[CurriculumSelector] read {path}: {e.Message}");
                    continue;
                }
                if (rec == null || rec.Placements == null || rec.Placements.Count == 0) continue;

                _placement.Clear();
                int placed = 0;
                bool brokenPiece = false;
                for (int i = 0; i < rec.Placements.Count; i++)
                {
                    var p = rec.Placements[i];
                    var r = _placement.TryPlace(
                        new TrackPieceShape(p.ShapeId),
                        new GridPosition(p.X, p.Y),
                        (TrackDirection)p.Facing);
                    if (!r.Success)
                    {
                        brokenPiece = true;
                        Debug.LogWarning($"[CurriculumSelector] replay {Path.GetFileName(path)} failed at piece {i}/{rec.Placements.Count}: {r.Reason}");
                        break;
                    }
                    placed++;
                }

                if (brokenPiece || !_loop.TryGetCurrentLoop(out var closed))
                {
                    _placement.Clear();
                    continue;
                }

                string circuitName = Path.GetFileNameWithoutExtension(path);
                Debug.Log($"[CurriculumSelector] circuit={circuitName} pieces={placed} length={closed.TotalLength:F1}");
                TrainingTelemetryContext.LastCircuitId = circuitName;
                TrainingTelemetry.EmitCircuitChange(circuitName, placed, closed.TotalLength);
                return GenerationResult.Ok(closed.Id, placed, closed.TotalLength);
            }

            Debug.LogError($"[CurriculumSelector] All 4 replay attempts failed for dir={dir}; reusing previous loop.");
            return GenerationResult.Failed("library replay exhausted");
        }

        private static string FindForcedCircuit(string id)
        {
            // Search the authored-closure library for "<id>.json".
            string libDir = CurriculumStages.LibraryDir;
            if (!Directory.Exists(libDir)) return null;
            string p = Path.Combine(libDir, id + ".json");
            return File.Exists(p) ? p : null;
        }

        private GenerationResult TryReplayPath(string path)
        {
            CircuitFile rec;
            try { rec = JsonUtility.FromJson<CircuitFile>(File.ReadAllText(path)); }
            catch (Exception e)
            {
                Debug.LogWarning($"[CurriculumSelector] read {path}: {e.Message}");
                return GenerationResult.Failed("read failed");
            }
            if (rec == null || rec.Placements == null || rec.Placements.Count == 0)
                return GenerationResult.Failed("empty record");

            _placement.Clear();
            int placed = 0;
            for (int i = 0; i < rec.Placements.Count; i++)
            {
                var p = rec.Placements[i];
                var r = _placement.TryPlace(
                    new TrackPieceShape(p.ShapeId),
                    new GridPosition(p.X, p.Y),
                    (TrackDirection)p.Facing);
                if (!r.Success)
                {
                    Debug.LogWarning($"[CurriculumSelector] forced replay {Path.GetFileName(path)} failed at piece {i}: {r.Reason}");
                    _placement.Clear();
                    return GenerationResult.Failed(r.Reason);
                }
                placed++;
            }
            if (!_loop.TryGetCurrentLoop(out var closed))
            {
                _placement.Clear();
                return GenerationResult.Failed("loop not closed");
            }
            Debug.Log($"[CurriculumSelector] FORCED circuit={Path.GetFileNameWithoutExtension(path)} pieces={placed} length={closed.TotalLength:F1}");
            return GenerationResult.Ok(closed.Id, placed, closed.TotalLength);
        }

        private static List<string> GetFiles(string dir)
        {
            // Rescan on every call so newly authored circuits are picked up
            // without restarting training. Folder is small (≤ a few hundred
            // files) and Generate runs once per episode; cost is negligible.
            var fresh = new List<string>();
            if (Directory.Exists(dir))
            {
                foreach (var f in Directory.GetFiles(dir, "*.json"))
                    fresh.Add(f);
            }
            return fresh;
        }

        [Serializable]
        private sealed class CircuitFile
        {
            public string Id;
            public float TotalLength;
            public int AnchorCount;
            public List<Placement> Placements;

            [Serializable]
            public sealed class Placement
            {
                public string ShapeId;
                public int X;
                public int Y;
                public int Facing;
            }
        }
    }
}
