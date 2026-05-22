using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.Track;
using UnityPpoRacingTrainer.Core.Track.Loop;
using UnityPpoRacingTrainer.Core.Track.Shape;
using Unidad.Core.EventBus;
using Unidad.Core.Systems;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.AiDriver.Training.Generation
{
    /// <summary>
    /// Recipe-based loop generator. Each call picks a parametric closed-loop
    /// recipe (a hand-crafted sequence of <see cref="TrackStep"/>s known to
    /// close back on itself for any parameter choice in range), instantiates
    /// it as a synthetic <see cref="TrackShape"/>, and submits the whole
    /// shape to <see cref="IShapePlacementService"/> in one atomic call.
    /// <list type="bullet">
    ///   <item>Recipes are <i>closed by construction</i>: every recipe's step
    ///   path returns to (0,0) heading north regardless of parameter values,
    ///   so the placement either succeeds end-to-end or fails fast on a
    ///   bounds / overlap / terrain conflict.</item>
    ///   <item>The recipe palette is the variety surface: oval, rectangle,
    ///   stretched oval, left-turning mirror, etc. Adding more recipes
    ///   widens layout variety; each is only a few lines.</item>
    /// </list>
    /// </summary>
    internal sealed class ShapeBasedLoopGenerator : SystemServiceBase, IProceduralLoopGenerator
    {
        private readonly IShapePlacementService _shapePlacement;
        // ITrackShapeCatalog + ITrackPlacementService + IClosedLoopService kept
        // on the ctor for future variety (e.g. mid-loop catalog-shape
        // substitution) but not strictly needed by the recipe-only path. The
        // shape placement service does all the per-piece validation +
        // placement we care about today.
        private readonly ITrackShapeCatalog _shapeCatalog;
        private readonly ITrackPlacementService _placement;
        private readonly IClosedLoopService _loop;

        private bool _closedThisRun;
        private int _closedLoopId;
        private float _closedLoopLength;

        public ShapeBasedLoopGenerator(
            IEventBus eventBus,
            IShapePlacementService shapePlacement,
            ITrackShapeCatalog shapeCatalog,
            ITrackPlacementService placement,
            IClosedLoopService loop) : base(eventBus)
        {
            _shapePlacement = shapePlacement;
            _shapeCatalog = shapeCatalog;
            _placement = placement;
            _loop = loop;
        }

        public GenerationResult Generate(in GenerationConfig cfg)
        {
            var rng = new System.Random(cfg.Seed);

            _closedThisRun = false;
            _closedLoopId = 0;
            _closedLoopLength = 0f;
            using var sub = EventBus.Subscribe<LoopClosedEvent>(OnLoopClosed);

            // Try recipes in random order. First one whose pieces all validate wins.
            // A given recipe may fail at certain origins/sizes (e.g. a long-oval
            // bumping the terrain bound) so retry on rejection rather than abort.
            int recipeAttempts = Recipes.Length * 2;
            for (int attempt = 0; attempt < recipeAttempts; attempt++)
            {
                var recipe = Recipes[rng.Next(Recipes.Length)];
                var steps = recipe.Build(rng);
                var shape = new TrackShape(
                    new TrackShapeId($"PROC_{cfg.Seed}_{attempt}"),
                    $"Procedural ({recipe.Name})",
                    steps);

                var result = _shapePlacement.TryPlaceShape(shape, cfg.Origin, cfg.InitialFacing);
                if (result.Success)
                {
                    if (_closedThisRun)
                    {
                        TrainingTelemetryContext.LastCircuitId = shape.Id.Id;
                        TrainingTelemetry.EmitCircuitChange(shape.Id.Id, shape.Pieces.Count, _closedLoopLength);
                        return GenerationResult.Ok(_closedLoopId, shape.Pieces.Count, _closedLoopLength);
                    }
                    // Placement reported success but closure didn't fire — almost
                    // certainly a recipe bug. Tear down and report so it surfaces
                    // loudly during dev.
                    _placement.Clear();
                    return GenerationResult.Failed(
                        $"Recipe '{recipe.Name}' placed {shape.Pieces.Count} pieces but ClosedLoopService didn't detect closure");
                }

                // Validators rejected; clear any partials the placement service may
                // have left and try a different recipe / parameters.
                _placement.Clear();
            }

            return GenerationResult.Failed(
                $"No recipe fit at origin={cfg.Origin} after {recipeAttempts} attempts");
        }

        private void OnLoopClosed(LoopClosedEvent evt)
        {
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

        // ---- recipes ----

        private static readonly Recipe[] Recipes =
        {
            // Rectangles in 5 size brackets (right- and left-handed corners).
            new RectangleRecipe(rightHanded: true,  minN: 2, maxN: 6, minE: 1, maxE: 4),
            new RectangleRecipe(rightHanded: false, minN: 2, maxN: 6, minE: 1, maxE: 4),
            new RectangleRecipe(rightHanded: true,  minN: 4, maxN: 9, minE: 1, maxE: 2),
            new RectangleRecipe(rightHanded: true,  minN: 1, maxN: 3, minE: 1, maxE: 3),
            new RectangleRecipe(rightHanded: false, minN: 4, maxN: 8, minE: 1, maxE: 3),
            // Ovals with an S-curve injected on each long side (mirrored, so the
            // lateral offsets cancel and the loop still closes).
            new SCurveOvalRecipe(rightHanded: true,  minN: 5, maxN: 9, minE: 2, maxE: 4),
            new SCurveOvalRecipe(rightHanded: false, minN: 5, maxN: 9, minE: 2, maxE: 4),
            // Ovals with a Chicane (F R F L F, 5 pieces) on each long side.
            new ChicaneOvalRecipe(rightHanded: true,  minN: 6, maxN: 10, minE: 2, maxE: 4),
            new ChicaneOvalRecipe(rightHanded: false, minN: 6, maxN: 10, minE: 2, maxE: 4),
        };

        private abstract class Recipe
        {
            public abstract string Name { get; }
            public abstract TrackStep[] Build(System.Random rng);

            protected static void Forwards(List<TrackStep> s, int count)
            {
                for (int i = 0; i < count; i++) s.Add(TrackStep.Forward);
            }

            // F R F — a 90° right corner. The leading + trailing F give the corner a
            // straight tile each side so the resulting chain isn't pure curves.
            protected static void RightCorner(List<TrackStep> s)
            {
                s.Add(TrackStep.Forward);
                s.Add(TrackStep.TurnRight);
                s.Add(TrackStep.Forward);
            }

            protected static void LeftCorner(List<TrackStep> s)
            {
                s.Add(TrackStep.Forward);
                s.Add(TrackStep.TurnLeft);
                s.Add(TrackStep.Forward);
            }

            /// <summary>
            /// Inserts a wiggle (S-curve or chicane) symmetrically into both long sides
            /// so the lateral offset on side 1 (heading north, +1 X) is cancelled by
            /// the same step pattern on side 3 (heading south, -1 X). The plain
            /// rectangle's closure math is preserved.
            ///
            /// SCurve   = F R L F  → 4 pieces, 3 longitudinal, +1 lateral.
            /// Chicane  = F R F L F → 5 pieces, 4 longitudinal, +1 lateral.
            /// </summary>
            protected static void AddWiggleSide(List<TrackStep> s, int n, int wiggleLongitudinal, Action<List<TrackStep>> wiggleEmit)
            {
                // Place the wiggle in the middle of the side: K straights, wiggle, rest of straights.
                int leftover = n - wiggleLongitudinal;
                int k = Math.Max(0, leftover / 2);
                Forwards(s, k);
                wiggleEmit(s);
                Forwards(s, leftover - k);
            }
        }

        /// <summary>
        /// 4-corner rectangular loop. Side lengths are independently random within
        /// their stage-given ranges. Closes by construction: 4 right turns return
        /// the heading to its starting direction; symmetric forward counts mean
        /// the cursor lands one cell south of origin and the final corner walks it
        /// back to (0,0). The last placement is at (0,-1), one cell before origin —
        /// the chain extractor sees the (0,-1)→(0,0) port boundary as the closure
        /// seam.
        /// </summary>
        private sealed class RectangleRecipe : Recipe
        {
            private readonly bool _rightHanded;
            private readonly int _minN, _maxN, _minE, _maxE;

            public RectangleRecipe(bool rightHanded, int minN, int maxN, int minE, int maxE)
            {
                _rightHanded = rightHanded;
                _minN = minN; _maxN = maxN; _minE = minE; _maxE = maxE;
            }

            public override string Name =>
                _rightHanded ? "Rectangle-R" : "Rectangle-L";

            public override TrackStep[] Build(System.Random rng)
            {
                int n = _minN + rng.Next(_maxN - _minN + 1);
                int e = _minE + rng.Next(_maxE - _minE + 1);
                var s = new List<TrackStep>();
                Action<List<TrackStep>> corner = _rightHanded ? (Action<List<TrackStep>>)RightCorner : LeftCorner;

                Forwards(s, n); corner(s);
                Forwards(s, e); corner(s);
                Forwards(s, n); corner(s);
                Forwards(s, e); corner(s);
                return s.ToArray();
            }
        }

        /// <summary>
        /// Rectangle with an S-curve injected into each long side. The two S-curves
        /// have identical step patterns; because side 1 walks north and side 3 walks
        /// south, the lateral offsets are equal-and-opposite in absolute X and cancel.
        /// Loop still closes.
        /// </summary>
        private sealed class SCurveOvalRecipe : Recipe
        {
            private readonly bool _rightHanded;
            private readonly int _minN, _maxN, _minE, _maxE;

            public SCurveOvalRecipe(bool rightHanded, int minN, int maxN, int minE, int maxE)
            {
                _rightHanded = rightHanded;
                _minN = minN; _maxN = maxN; _minE = minE; _maxE = maxE;
            }

            public override string Name => _rightHanded ? "SCurveOval-R" : "SCurveOval-L";

            public override TrackStep[] Build(System.Random rng)
            {
                int n = _minN + rng.Next(_maxN - _minN + 1);
                int e = _minE + rng.Next(_maxE - _minE + 1);
                var s = new List<TrackStep>();
                Action<List<TrackStep>> corner = _rightHanded ? (Action<List<TrackStep>>)RightCorner : LeftCorner;
                Action<List<TrackStep>> emit = _rightHanded ? (Action<List<TrackStep>>)EmitRightS : EmitLeftS;

                AddWiggleSide(s, n, wiggleLongitudinal: 3, wiggleEmit: emit); corner(s);
                Forwards(s, e); corner(s);
                AddWiggleSide(s, n, wiggleLongitudinal: 3, wiggleEmit: emit); corner(s);
                Forwards(s, e); corner(s);
                return s.ToArray();
            }

            // F R L F — right-handed S-curve. From canonical (north) start, advances
            // 3 longitudinal + 1 right-lateral.
            private static void EmitRightS(List<TrackStep> s)
            {
                s.Add(TrackStep.Forward); s.Add(TrackStep.TurnRight);
                s.Add(TrackStep.TurnLeft); s.Add(TrackStep.Forward);
            }
            // F L R F — mirror.
            private static void EmitLeftS(List<TrackStep> s)
            {
                s.Add(TrackStep.Forward); s.Add(TrackStep.TurnLeft);
                s.Add(TrackStep.TurnRight); s.Add(TrackStep.Forward);
            }
        }

        /// <summary>
        /// Rectangle with a Chicane (F R F L F = 5 pieces, 4 longitudinal, +1 lateral)
        /// on each long side. Same lateral-cancellation argument as the S-curve recipe.
        /// </summary>
        private sealed class ChicaneOvalRecipe : Recipe
        {
            private readonly bool _rightHanded;
            private readonly int _minN, _maxN, _minE, _maxE;

            public ChicaneOvalRecipe(bool rightHanded, int minN, int maxN, int minE, int maxE)
            {
                _rightHanded = rightHanded;
                _minN = minN; _maxN = maxN; _minE = minE; _maxE = maxE;
            }

            public override string Name => _rightHanded ? "ChicaneOval-R" : "ChicaneOval-L";

            public override TrackStep[] Build(System.Random rng)
            {
                int n = _minN + rng.Next(_maxN - _minN + 1);
                int e = _minE + rng.Next(_maxE - _minE + 1);
                var s = new List<TrackStep>();
                Action<List<TrackStep>> corner = _rightHanded ? (Action<List<TrackStep>>)RightCorner : LeftCorner;
                Action<List<TrackStep>> emit = _rightHanded ? (Action<List<TrackStep>>)EmitRightChicane : EmitLeftChicane;

                AddWiggleSide(s, n, wiggleLongitudinal: 4, wiggleEmit: emit); corner(s);
                Forwards(s, e); corner(s);
                AddWiggleSide(s, n, wiggleLongitudinal: 4, wiggleEmit: emit); corner(s);
                Forwards(s, e); corner(s);
                return s.ToArray();
            }

            private static void EmitRightChicane(List<TrackStep> s)
            {
                s.Add(TrackStep.Forward); s.Add(TrackStep.TurnRight);
                s.Add(TrackStep.Forward);
                s.Add(TrackStep.TurnLeft); s.Add(TrackStep.Forward);
            }
            private static void EmitLeftChicane(List<TrackStep> s)
            {
                s.Add(TrackStep.Forward); s.Add(TrackStep.TurnLeft);
                s.Add(TrackStep.Forward);
                s.Add(TrackStep.TurnRight); s.Add(TrackStep.Forward);
            }
        }
    }
}
