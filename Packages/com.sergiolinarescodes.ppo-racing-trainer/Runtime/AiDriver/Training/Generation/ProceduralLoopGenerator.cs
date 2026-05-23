using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.AiDriver.Training.Curriculum;
using UnityPpoRacingTrainer.Core.Track;
using UnityPpoRacingTrainer.Core.Track.Loop;
using Unidad.Core.EventBus;
using Unidad.Core.Grid;
using Unidad.Core.Systems;

namespace UnityPpoRacingTrainer.Core.AiDriver.Training.Generation
{
    /// <summary>
    /// Random-walk loop generator. While <c>placedCount &lt; stage.MinPiecesBeforeClose</c>
    /// each step's primary pick comes from <see cref="LoopBuilder.PickPiece"/> (RNG-weighted).
    /// After that, picks are biased greedy toward the start cell so the walk converges.
    /// Per-step fallback ordering (straight → right curve → left curve) exists so a
    /// rejected primary doesn't kill the walk; once all three fallbacks fail at one
    /// cell, generation aborts with a "stalled" reason.
    ///
    /// Closure detection is event-driven: the generator subscribes to
    /// <see cref="LoopClosedEvent"/> for the duration of a single <see cref="Generate"/>
    /// call, and stops as soon as <c>ClosedLoopService</c>'s synchronous rebuild fires it.
    /// </summary>
    internal sealed class ProceduralLoopGenerator : SystemServiceBase, IProceduralLoopGenerator
    {
        private readonly ITrackPlacementService _placement;
        private readonly IClosedLoopService _loop;

        private bool _closedThisRun;
        private int _closedLoopId;
        private float _closedLoopLength;

        public ProceduralLoopGenerator(
            IEventBus eventBus,
            ITrackPlacementService placement,
            IClosedLoopService loop) : base(eventBus)
        {
            _placement = placement;
            _loop = loop;
        }

        public GenerationResult Generate(in GenerationConfig cfg)
        {
            var stage = cfg.Stage;
            if (stage.MaxPieces < stage.MinPieces || stage.MinPieces < 4)
                return GenerationResult.Failed(
                    $"Invalid stage '{stage.Name}': MinPieces={stage.MinPieces}, MaxPieces={stage.MaxPieces}");

            _closedThisRun = false;
            _closedLoopId = 0;
            _closedLoopLength = 0f;

            // Local subscription — released even if Generate() throws.
            using var sub = EventBus.Subscribe<LoopClosedEvent>(OnLoopClosed);
            return RunGenerate(cfg);
        }

        private GenerationResult RunGenerate(in GenerationConfig cfg)
        {
            var stage = cfg.Stage;
            var rng = new Random(cfg.Seed);
            var visited = new HashSet<GridPosition> { cfg.Origin };
            var history = new Stack<HistoryFrame>();

            var pos = cfg.Origin;
            var heading = cfg.InitialFacing;
            int placed = 0;
            int backtracks = 0;

            while (placed < stage.MaxPieces && !_closedThisRun)
            {
                bool placedThisStep = false;

                foreach (var pick in OrderedAttempts(pos, heading, cfg.Origin, cfg.InitialFacing, placed, stage, rng))
                {
                    var (dx, dz) = pick.NextHeading.Step();
                    var nextPos = new GridPosition(pos.X + dx, pos.Y + dz);

                    // Closing back onto origin is allowed; revisiting any other cell isn't.
                    if (nextPos != cfg.Origin && visited.Contains(nextPos)) continue;

                    var result = _placement.TryPlace(pick.Shape, pos, pick.PlacementFacing);
                    if (!result.Success) continue;

                    history.Push(new HistoryFrame(pos, heading, result.Id, nextPos));
                    placed++;
                    pos = nextPos;
                    heading = pick.NextHeading;
                    visited.Add(pos);
                    placedThisStep = true;
                    break;
                }

                if (placedThisStep) continue;

                // No valid placement at this cell. Backtrack one step if we still have
                // budget; removing the last piece may re-open a previously-closed chain
                // (which is fine — we're still searching).
                if (backtracks < stage.MaxBacktracks && history.Count > 0)
                {
                    var frame = history.Pop();
                    _placement.Remove(frame.PlacedId);
                    visited.Remove(frame.NextPos);
                    pos = frame.PrevPos;
                    heading = frame.PrevHeading;
                    placed--;
                    backtracks++;
                    continue;
                }

                return GenerationResult.Failed(
                    $"Stalled at {pos} after {placed} pieces, backtracks={backtracks}/{stage.MaxBacktracks} " +
                    $"(stage '{stage.Name}', seed {cfg.Seed})");
            }

            if (_closedThisRun)
                return GenerationResult.Ok(_closedLoopId, placed, _closedLoopLength);

            // Hit MaxPieces without closure. Caller can retry with a fresh seed.
            return GenerationResult.Failed(
                $"Loop did not close within {stage.MaxPieces} pieces (placed {placed}, stage '{stage.Name}', seed {cfg.Seed})");
        }

        private readonly record struct HistoryFrame(
            GridPosition PrevPos,
            TrackDirection PrevHeading,
            TrackPieceId PlacedId,
            GridPosition NextPos);

        // Ordered attempts: closure-biased primary first, then deterministic fallbacks.
        // Yielded as IEnumerable so the caller can break out as soon as one succeeds.
        private static IEnumerable<LoopBuilder.Pick> OrderedAttempts(
            GridPosition pos, TrackDirection heading,
            GridPosition origin, TrackDirection startHeading,
            int placedCount, CurriculumStage stage, Random rng)
        {
            var primary = (placedCount >= stage.MinPiecesBeforeClose)
                ? PickClosureBiased(pos, heading, origin, startHeading)
                : LoopBuilder.PickPiece(stage, heading, rng);
            yield return primary;

            // Stable fallback order. Skip the kind we just yielded.
            foreach (var kind in FallbackKinds)
            {
                if (kind == primary.Kind) continue;
                yield return LoopBuilder.BuildPick(kind, heading);
            }
        }

        private static readonly LoopBuilder.PieceKind[] FallbackKinds =
        {
            LoopBuilder.PieceKind.Straight,
            LoopBuilder.PieceKind.RightCurve,
            LoopBuilder.PieceKind.LeftCurve,
        };

        // Closure routing: drive the cursor toward
        //   target = origin - startHeading.Step()
        // (the cell whose exit port aligns with the first piece's entry port). At
        // target, place a closing piece chosen so its exit port hits the origin's
        // entry boundary:
        //   heading == startHeading                       → Straight  (exits forward)
        //   heading == startHeading.RotateLeft()          → RightCurve (exits to the right = startHeading)
        //   heading == startHeading.RotateRight()         → LeftCurve  (exits to the left  = startHeading)
        //   heading == startHeading.Opposite()            → RightCurve to start a U-turn detour
        // Off-target: greedy on Manhattan distance to target with tie-break
        // preferring Straights (no turn to undo).
        private static LoopBuilder.Pick PickClosureBiased(
            GridPosition pos, TrackDirection heading,
            GridPosition origin, TrackDirection startHeading)
        {
            var (sx, sy) = startHeading.Step();
            var target = new GridPosition(origin.X - sx, origin.Y - sy);

            if (pos == target)
            {
                if (heading == startHeading)
                    return LoopBuilder.BuildPick(LoopBuilder.PieceKind.Straight, heading);
                if (heading == startHeading.RotateLeft())
                    return LoopBuilder.BuildPick(LoopBuilder.PieceKind.RightCurve, heading);
                if (heading == startHeading.RotateRight())
                    return LoopBuilder.BuildPick(LoopBuilder.PieceKind.LeftCurve, heading);
                // heading == startHeading.Opposite() — kick off a U-turn detour.
                return LoopBuilder.BuildPick(LoopBuilder.PieceKind.RightCurve, heading);
            }

            int curDist = Math.Abs(target.X - pos.X) + Math.Abs(target.Y - pos.Y);
            LoopBuilder.PieceKind best = LoopBuilder.PieceKind.RightCurve;
            int bestRank = -1;
            int bestPriority = -1;

            foreach (var kind in FallbackKinds)
            {
                var pick = LoopBuilder.BuildPick(kind, heading);
                var (mx, my) = pick.NextHeading.Step();
                var nextPos = new GridPosition(pos.X + mx, pos.Y + my);
                int newDist = Math.Abs(target.X - nextPos.X) + Math.Abs(target.Y - nextPos.Y);

                int rank;
                if (nextPos == target) rank = 3;        // arrives at target this step
                else if (newDist < curDist) rank = 2;   // strictly closer
                else if (newDist == curDist) rank = 1;  // sideways
                else rank = 0;                          // worse

                // Tie-break: Straight > LeftCurve > RightCurve. Straights don't add
                // a turn we'd have to undo; RightCurve is reserved as the U-turn
                // fallback for the "all options bad" case.
                int priority = kind switch
                {
                    LoopBuilder.PieceKind.Straight => 2,
                    LoopBuilder.PieceKind.LeftCurve => 1,
                    LoopBuilder.PieceKind.RightCurve => 0,
                    _ => -1,
                };

                if (rank > bestRank || (rank == bestRank && priority > bestPriority))
                {
                    best = kind;
                    bestRank = rank;
                    bestPriority = priority;
                }
            }

            // If every option scored "worse" (rank 0), no greedy move helps — fall
            // back to a right turn to start a U-turn.
            if (bestRank == 0)
                return LoopBuilder.BuildPick(LoopBuilder.PieceKind.RightCurve, heading);

            return LoopBuilder.BuildPick(best, heading);
        }

        private void OnLoopClosed(LoopClosedEvent evt)
        {
            // First closure within this Generate() call wins. Subsequent closures
            // (e.g. if a previously-placed loop was already in the system) are ignored
            // by the closed flag, but TotalLength + LoopId are taken from the live
            // ClosedLoopService below to stay consistent with the chain that's actually
            // still on the grid.
            if (_closedThisRun) return;
            _closedThisRun = true;
            if (_loop.TryGetCurrentLoop(out var l))
            {
                _closedLoopId = l.Id;
                _closedLoopLength = l.TotalLength;
            }
            else
            {
                _closedLoopId = evt.LoopId;
                _closedLoopLength = evt.TotalLength;
            }
        }
    }
}
