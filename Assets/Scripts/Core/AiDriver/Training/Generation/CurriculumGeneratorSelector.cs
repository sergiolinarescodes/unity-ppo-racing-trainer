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
    /// authored-closure circuit on every episode, regardless of stage id.
    /// Stage id is still consumed by the reward shaper for feature gating
    /// (fuel / tire / opponents / personality archetype). <see cref="ShapeBasedLoopGenerator"/>
    /// is retained ONLY as a crash-time fallback for the empty-library case
    /// (it should never fire in production — the authored corpus has ~92
    /// circuits).
    /// </summary>
    internal sealed class CurriculumGeneratorSelector : IProceduralLoopGenerator
    {
        private readonly ShapeBasedLoopGenerator _shapeBased;
        private readonly ITrackPlacementService _placement;
        private readonly IClosedLoopService _loop;
        private readonly Dictionary<int, List<string>> _libraryFiles = new();
        private readonly System.Random _rng = new();

        /// <summary>
        /// When set to a non-empty 8-char circuit id (e.g. "08c66d8e"), the
        /// selector loads ONLY that circuit from any library stage's directory
        /// and replays it on every Generate call. Empty = random pick.
        /// Set by <c>TrainerBootstrap.forceCircuitId</c> for editor inference.
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
            // Forced-id wins over stage routing: if a specific circuit is
            // pinned (editor inference / debugging), replay it directly even
            // when the stage maps to recipe generation, an empty library dir,
            // or the authored corpus. The caller is responsible for clearing
            // the field before resuming production training.
            string forced = ForcedCircuitId;
            if (!string.IsNullOrEmpty(forced))
            {
                string forcedPath = FindForcedCircuit(forced);
                if (forcedPath != null)
                {
                    var result = TryReplayPath(forcedPath);
                    if (result.Success) return result;
                    Debug.LogWarning($"[CurriculumSelector] Forced circuit '{forced}' failed to replay; falling back to stage routing.");
                }
                else
                {
                    Debug.LogWarning($"[CurriculumSelector] Forced circuit id '{forced}' not found in any library; falling back to stage routing.");
                }
            }

            string libDir = CurriculumStages.LibraryDirFor(cfg.Stage.Id);
            if (libDir == null)
                return _shapeBased.Generate(cfg);

            return TryReplayFromLibrary(cfg.Stage.Id, libDir, cfg);
        }

        private GenerationResult TryReplayFromLibrary(int stageId, string dir, in GenerationConfig cfg)
        {
            var files = GetFiles(stageId, dir);
            if (files.Count == 0)
            {
                Debug.LogWarning($"[CurriculumSelector] Library '{dir}' empty for stage {stageId}; falling back to ShapeBased.");
                return _shapeBased.Generate(cfg);
            }

            // ForcedCircuitId override: locate the JSON whose filename
            // matches (without extension) and replay it. If it is not in
            // this stage's dir, search all library dirs. If still not found,
            // fall through to a random pick (warn once). Honoured on every
            // stage including authored; clear the field on the bootstrap to
            // resume rotation.
            string forced = ForcedCircuitId;
            if (!string.IsNullOrEmpty(forced))
            {
                string forcedPath = FindForcedCircuit(forced);
                if (forcedPath != null)
                {
                    var result = TryReplayPath(forcedPath);
                    if (result.Success) return result;
                    Debug.LogWarning($"[CurriculumSelector] Forced circuit '{forced}' failed to replay; falling back to random.");
                }
                else
                {
                    Debug.LogWarning($"[CurriculumSelector] Forced circuit id '{forced}' not found in any library; falling back to random.");
                }
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
                Debug.Log($"[CurriculumSelector] stage={stageId} circuit={circuitName} pieces={placed} length={closed.TotalLength:F1}");
                TrainingTelemetryContext.LastCircuitId = circuitName;
                TrainingTelemetry.EmitCircuitChange(circuitName, placed, closed.TotalLength);
                return GenerationResult.Ok(closed.Id, placed, closed.TotalLength);
            }

            Debug.LogError($"[CurriculumSelector] All 4 replay attempts failed for stage {stageId} dir={dir}; reusing previous loop.");
            return GenerationResult.Failed("library replay exhausted");
        }

        private string FindForcedCircuit(string id)
        {
            // Search circuits/stage_*/ directories for "<id>.json".
            const string root = "circuits";
            if (!Directory.Exists(root)) return null;
            foreach (var stageDir in Directory.GetDirectories(root, "stage_*"))
            {
                string p = Path.Combine(stageDir, id + ".json");
                if (File.Exists(p)) return p;
            }
            return null;
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

        private List<string> GetFiles(int stageId, string dir)
        {
            // Every stage now replays from the authored-closure library, so
            // rescan on every call — newly authored circuits are picked up
            // without restarting training. Folder is small (≤ a few hundred
            // files) and Generate runs once per episode; cost is negligible.
            var fresh = new List<string>();
            if (Directory.Exists(dir))
            {
                foreach (var f in Directory.GetFiles(dir, "*.json"))
                    fresh.Add(f);
            }
            _libraryFiles[stageId] = fresh;
            return fresh;
        }

        [Serializable]
        private sealed class CircuitFile
        {
            public string Id;
            public int StageId;
            public string StageName;
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
