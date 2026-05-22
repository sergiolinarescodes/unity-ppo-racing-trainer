using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityPpoRacingTrainer.Core.AiDriver.Training.Generation;
using UnityPpoRacingTrainer.Core.Terrain;
using UnityPpoRacingTrainer.Core.Track.Generation.Realistic.Native;
using UnityPpoRacingTrainer.Core.Track.Loop;
using UnityPpoRacingTrainer.Core.Track.Ribbon;
using UnityPpoRacingTrainer.Core.Track.Shape;
using Unidad.Core.EventBus;
using Unidad.Core.Grid;
using Unidad.Core.Systems;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace UnityPpoRacingTrainer.Core.Track.Generation.Realistic
{
    /// <summary>
    /// F1-flavoured procedural track generator. The recipe palette is gone —
    /// this generator drives a Burst-compiled beam search over the player's
    /// full shape catalog. Each step expands all (shape, anchor port, mirror)
    /// candidates in parallel, scores them by a heuristic that balances target
    /// length, turn density, F1 flavour and closure proximity, and commits the
    /// best move via <see cref="IShapePlacementService"/>. When a closure-eligible
    /// candidate fires, a precise managed Tier-1 closure attempt snaps a final
    /// shape across the seam between frontier and entry. With no Tier-1 hit
    /// before max steps, the attempt fails and the outer loop retries with a
    /// new seed offset. There is no runtime Bezier-synthesis fallback — closure
    /// must come from a real catalog shape (authored card, recipe shape, or
    /// closure-only transition primitive registered by
    /// <see cref="ClosureTransitionShapes"/>).
    /// </summary>
    internal sealed class RealisticTrackGenerator : SystemServiceBase, IRealisticTrackGenerator, IDisposable
    {
        private readonly ITrackPlacementService _placement;
        private readonly IShapePlacementService _shapePlacement;
        private readonly IClosedLoopService _loop;
        private readonly ITrackShapeCatalog _shapeCatalog;
        private readonly ITrackPieceCatalog _pieceCatalog;
        private readonly ITerrainService _terrain;
        private readonly RealisticNativeCatalog _native;

        private NativeArray<CandidateSlot> _slots;
        private NativeArray<CandidateSlot> _activeSlotsBuffer;
        private NativeArray<Candidate> _outputBuffer;
        private NativeArray<short> _shapeUseCounts;
        private float[] _scoreSnap = Array.Empty<float>();
        private int[] _orderSnap = Array.Empty<int>();

        private bool _closedThisRun;
        private int _closedLoopId;
        private float _closedLoopLength;

        // Per-Generate tracking: piece IDs that were committed by closure-only
        // shapes (id prefix "closure:"). The export pipeline (e.g.
        // AuthoredClosureCircuitLibrary) uses this to colour closure
        // transitions differently from authored body cards in the Python
        // viewer. Cleared at the start of each Generate run.
        private readonly HashSet<TrackPieceId> _closurePieceIds = new();
        internal IReadOnlyCollection<TrackPieceId> LastClosurePieceIds => _closurePieceIds;

        public RealisticTrackGenerator(
            IEventBus eventBus,
            ITrackPlacementService placement,
            IShapePlacementService shapePlacement,
            IClosedLoopService loop,
            ITrackShapeCatalog shapeCatalog,
            ITrackPieceCatalog pieceCatalog,
            ITerrainService terrain,
            RealisticNativeCatalog native) : base(eventBus)
        {
            _placement = placement;
            _shapePlacement = shapePlacement;
            _loop = loop;
            _shapeCatalog = shapeCatalog;
            _pieceCatalog = pieceCatalog;
            _terrain = terrain;
            _native = native;
        }

        public GenerationResult Generate(in GenerationConfig cfg)
            => Generate(RealisticTrackGenerationConfig.From(cfg));

        public GenerationResult Generate(in RealisticTrackGenerationConfig cfg)
        {
            EnsureSlotsBuilt();
            _closurePieceIds.Clear();
            using var sub = EventBus.Subscribe<LoopClosedEvent>(OnLoopClosed);

            int attempts = math.max(1, cfg.RecipeAttempts);
            string lastFailure = "no attempts run";
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                _closedThisRun = false;
                _closedLoopId = 0;
                _closedLoopLength = 0f;

                var result = TryGenerateAttempt(cfg, attempt, out lastFailure);
                if (result.Success)
                {
                    // Multi-chain guard: a successful LoopClosedEvent only means
                    // *some* sub-chain closed. If the placement produced more
                    // than one connected component (the user's two-ribbon bug),
                    // the layout has an unattached arm — visually wrong, race
                    // would also fail. Reject and retry.
                    int chainCount = ChainCount();
                    if (chainCount != 1)
                    {
                        lastFailure = $"multi-chain layout ({chainCount} chains)";
                        _placement.Clear();
                        continue;
                    }

                    // Closed-loop self-intersection guard. Reject layouts whose
                    // centerlines visually cross — the catalog's tile-overlap
                    // validator can't catch a curve that bows past a non-mating
                    // neighbour. Fall through to the next attempt with a different
                    // seed offset, eventually to ShapeBased fallback.
                    if (HasSelfIntersection())
                    {
                        lastFailure = "self-intersection in closed loop";
                        _placement.Clear();
                        continue;
                    }
                    Debug.Log($"[RealTrackGen] seed={cfg.Seed} stage={cfg.Stage.Name} " +
                              $"placed={result.PlacedPieces} length={result.TotalLength:F1} attempt={attempt}");
                    return result;
                }
                _placement.Clear();
            }
            return GenerationResult.Failed(
                $"Realistic generator: no closure within {attempts} attempts: {lastFailure}");
        }

        // Number of disjoint chains the extractor sees in the current placement.
        // A successful generation should produce exactly 1 chain — anything else
        // means the path branched (port-mate quantization mismatch, premature
        // closure, etc.).
        private int ChainCount()
        {
            var extractor = new TrackChainExtractor(_pieceCatalog);
            var chains = extractor.Extract(_placement.Placed);
            return chains?.Count ?? 0;
        }

        // Pairwise non-adjacent segment intersection on the chain centerline.
        // Detects strict crossings only — close-parallel near-misses are
        // handled by the per-candidate pre-placement guard
        // (CandidateWouldOverlapChain) so we don't over-reject closed loops
        // here. ChainAdjSkip skips an arc-length window around each sample's
        // neighbours so port joins (seam, corners) don't false-positive.
        private const int ChainAdjSkip = 18;  // ~1.4 cells of arc at RibbonSampleArcStep=0.08; covers the smoothing fan-out around port joins

        private bool HasSelfIntersection()
        {
            var extractor = new TrackChainExtractor(_pieceCatalog);
            var chains = extractor.Extract(_placement.Placed);
            for (int c = 0; c < chains.Count; c++)
            {
                var chain = chains[c];
                if (chain == null || chain.Length < 2) continue;
                var samples = CatmullRomSpline.Resample(chain, TrackPieceConstants.RibbonSampleArcStep);
                int n = samples.Count;
                bool isClosed = Vector3.Distance(samples[0].Position, samples[n - 1].Position)
                                < TrackPieceConstants.PortQuantizeGridSize * 4f;
                for (int i = 0; i + 1 < n; i++)
                {
                    Vector3 a0 = samples[i].Position;
                    Vector3 a1 = samples[i + 1].Position;
                    for (int j = i + 1 + ChainAdjSkip; j + 1 < n; j++)
                    {
                        if (isClosed && (n - j + i) <= ChainAdjSkip) continue;
                        Vector3 b0 = samples[j].Position;
                        Vector3 b1 = samples[j + 1].Position;
                        if (Segments2DIntersect(a0, a1, b0, b1))
                            return true;
                    }
                }
            }
            return false;
        }

        private static bool Segments2DIntersect(Vector3 p1, Vector3 p2, Vector3 p3, Vector3 p4)
        {
            // Standard 2D segment intersection on the XZ plane. Uses the sign
            // of the 2x2 determinants; treats colinear-overlap as a non-cross
            // (chain extractor's port joins land here and shouldn't be flagged).
            float d1 = Cross(p3, p4, p1);
            float d2 = Cross(p3, p4, p2);
            float d3 = Cross(p1, p2, p3);
            float d4 = Cross(p1, p2, p4);
            const float eps = 1e-3f;
            if (((d1 > eps && d2 < -eps) || (d1 < -eps && d2 > eps))
                && ((d3 > eps && d4 < -eps) || (d3 < -eps && d4 > eps)))
                return true;
            return false;
        }

        private static float Cross(Vector3 a, Vector3 b, Vector3 c)
            => (b.x - a.x) * (c.z - a.z) - (b.z - a.z) * (c.x - a.x);

        private GenerationResult TryGenerateAttempt(in RealisticTrackGenerationConfig cfg,
            int attempt, out string failureReason)
        {
            failureReason = "unknown";
            if (_native == null || !_native.IsBaked)
            {
                failureReason = "native catalog unbuilt";
                return GenerationResult.Failed(failureReason);
            }
            if (_terrain == null || !_terrain.IsInitialized)
            {
                failureReason = "terrain uninitialized";
                return GenerationResult.Failed(failureReason);
            }

            var sw = Stopwatch.StartNew();
            int timeoutMs = math.max(50, cfg.PerAttemptTimeoutMs);

            var anchorShapeIdx = FindAnchorShapeIdx(cfg);
            if (anchorShapeIdx < 0) { failureReason = "anchor shape missing"; return GenerationResult.Failed(failureReason); }

            // Place anchor, snapshot entry/frontier port poses.
            var anchorShapeId = _native.ShapeIds[anchorShapeIdx];
            if (!_shapeCatalog.TryGet(anchorShapeId, out var anchorShape))
            {
                failureReason = $"anchor shape '{anchorShapeId.Id}' not in shape catalog";
                return GenerationResult.Failed(failureReason);
            }
            var anchorPlace = _shapePlacement.TryPlaceShape(anchorShape, cfg.Origin, cfg.InitialFacing);
            TrackClosureCommit(anchorShape.Id, anchorPlace);
            if (!anchorPlace.Success)
            {
                failureReason = $"anchor placement rejected ({anchorPlace.InvalidCount}/{anchorPlace.TotalCount})";
                return GenerationResult.Failed(failureReason);
            }

            if (!ResolveEntryAndFrontier(anchorShapeIdx, cfg.Origin, cfg.InitialFacing,
                out var entry, out var frontier))
            {
                failureReason = "anchor entry/frontier resolution failed";
                return GenerationResult.Failed(failureReason);
            }

            int placedCount = anchorShape.Pieces.Count;
            int lengthCells = ShapeTotalCells(anchorShapeIdx);
            int lastShapeIdx = anchorShapeIdx;
            int netRotation = 0;
            int stuckRotationStreak = 0;

            // Reset per-attempt shape-use counts so each attempt builds its
            // own diversity profile. Anchor counts as one use of its shape.
            for (int i = 0; i < _shapeUseCounts.Length; i++) _shapeUseCounts[i] = 0;
            _shapeUseCounts[anchorShapeIdx] = 1;
            int shortCornerCount = 0;
            int uTurnCount = 0;
            int largeCurveCount = 0;
            int diagonalCount = 0;
            int consecutiveWiggleCount = 0;
            // Anchor is a long straight, so seed the consecutive-straight
            // counter with its cell count — the heuristic will push for the
            // first corner only after the minimum main-straight length.
            int consecutiveStraightCells = ShapeTotalCells(anchorShapeIdx);
            // Per-leg straight-floor jitter: each corner increments legIndex,
            // and the next leg's target floor is pulled from hash(seed,
            // attempt, legIndex). Range [floor*0.7, floor*1.4]. Produces
            // unequal legs across the loop → varied non-square shapes.
            int legIndex = 0;
            int currentLegFloor = JitterLegFloor(cfg, attempt, legIndex);

            var occupancy = new NativeHashMap<int2, byte>(256, Allocator.TempJob);
            StampShapeIntoOccupancy(anchorShapeIdx, IntFromGrid(cfg.Origin), (int)cfg.InitialFacing, ref occupancy);

            using var terrainSnap = _native is null
                ? default
                : RealisticNativeCatalog.SnapshotTerrain(_terrain, Allocator.TempJob);

            try
            {
                var weights = MakeWeights(cfg);

                int maxSteps = math.max(4, cfg.MaxSearchSteps);
                for (int step = 0; step < maxSteps; step++)
                {
                    if (sw.ElapsedMilliseconds >= timeoutMs)
                    {
                        failureReason = $"timeout after {sw.ElapsedMilliseconds}ms";
                        return GenerationResult.Failed(failureReason);
                    }

                    // Tier-1 closure first: cheap, ≤ shapes×ports² iterations,
                    // catches short-distance closures before paying for a full job.
                    // Gate by length AND placed-piece floor: closing too early
                    // produces the 2-5-piece micro-ovals (one mega-shape closes
                    // a 36-cell stub). Both gates must pass.
                    int minClosureLength = math.max(50, (int)(cfg.TargetLengthCells * 0.85f));
                    int minClosurePieces = math.max(20, (int)(cfg.TargetLengthCells * 0.3f));
                    if (lengthCells >= minClosureLength
                        && placedCount >= minClosurePieces
                        && TryCloseTier1(in entry, in frontier, in occupancy, in terrainSnap,
                                          lastShapeIdx, placedCount, out var closeResult))
                    {
                        return closeResult;
                    }

                    // Tier-2 in-step disabled: each DfsClose call (depth=N,
                    // ~64 branches/level) doesn't check timeout, so calling
                    // it per step caused 12+ min generations on hard seeds.
                    // Tier-2 still fires once on dead-end + once at maxSteps
                    // (later in this method) — that's enough closure budget.

                    // Beam-step: schedule Burst job, pick best forward candidate.
                    var fr = new FrontierState(
                        frontier.WorldCell, frontier.OutwardDir,
                        netRotation, lengthCells, lastShapeIdx,
                        shortCornerCount, uTurnCount, largeCurveCount,
                        diagonalCount, consecutiveWiggleCount,
                        consecutiveStraightCells, currentLegFloor);

                    if (!RunForwardStep(cfg, attempt, step,
                            entry, fr, weights,
                            occupancy.AsReadOnly(), terrainSnap,
                            out var picked))
                    {
                        // Body search dead-ended (terrain edge, no shape fits the frontier).
                        // Try Tier-2 closure-chain BFS before giving up the attempt — this
                        // is the slot the deleted Bezier fallback used to occupy, but with
                        // bounded multi-piece chains over the closure-only shape pool.
                        if (TryCloseTier2(in entry, in frontier, ref occupancy, in terrainSnap,
                                          placedCount, out var t2Result))
                        {
                            return t2Result;
                        }
                        failureReason = $"no valid forward candidate (step={step} placed={placedCount} length={lengthCells} netRot={netRotation} frontier=({frontier.WorldCell.x:F1},{frontier.WorldCell.y:F1})/{frontier.OutwardDir})";
                        return GenerationResult.Failed(failureReason);
                    }

                    if (!CommitCandidate(picked, ref occupancy, out var placedIds))
                    {
                        // Race between native validator and managed validator —
                        // managed rejected what native accepted. Stop attempt; outer
                        // retry will try a different seed offset.
                        failureReason = $"managed commit rejected native candidate at step {step}";
                        return GenerationResult.Failed(failureReason);
                    }

                    // Per-step centerline crossing guard. If this commit caused
                    // any non-adjacent segments in the placed-piece chain to
                    // cross visually (the user's X-intersection bug), revert
                    // the placement and abort this attempt — outer loop retries
                    // with a different seed offset.
                    if (HasSelfIntersection())
                    {
                        foreach (var id in placedIds)
                        {
                            _placement.Remove(id);
                        }
                        UnstampShapeFromOccupancy(picked.ShapeIdx, picked.Origin, picked.Facing,
                                                   ref occupancy);
                        failureReason = $"placement at step {step} would cause centerline crossing";
                        return GenerationResult.Failed(failureReason);
                    }

                    // Compute rotation delta BEFORE we update netRotation so
                    // the leg-break check sees the actual change.
                    int pickedRotDelta = picked.NetRotationAfter - netRotation;
                    bool pickedTurnsLeg = math.abs(pickedRotDelta) >= 2;

                    placedCount += ShapePieceCount(picked.ShapeIdx);
                    lengthCells = picked.LengthAfter;
                    netRotation = picked.NetRotationAfter;
                    lastShapeIdx = picked.ShapeIdx;
                    if (_shapeUseCounts[picked.ShapeIdx] < short.MaxValue)
                        _shapeUseCounts[picked.ShapeIdx]++;

                    // Update category counters from the committed shape's flags.
                    var pickedFlags = _native.Shapes[picked.ShapeIdx].KindFlags;
                    bool pickedShortCorner = (pickedFlags & ShapeKindFlags.IsShortCorner) != 0;
                    bool pickedUTurn = (pickedFlags & ShapeKindFlags.IsUTurn) != 0;
                    bool pickedLargeCurve = (pickedFlags & ShapeKindFlags.IsLargeCurve) != 0;
                    bool pickedDiagonal = (pickedFlags & ShapeKindFlags.IsDiagonalShape) != 0;
                    bool pickedStraight = (pickedFlags & ShapeKindFlags.IsPureStraight) != 0;
                    bool pickedWiggle = pickedLargeCurve || pickedUTurn;
                    if (pickedShortCorner) shortCornerCount++;
                    if (pickedUTurn) uTurnCount++;
                    if (pickedLargeCurve) largeCurveCount++;
                    if (pickedDiagonal) diagonalCount++;
                    if (pickedWiggle) consecutiveWiggleCount++;
                    else if (pickedStraight) consecutiveWiggleCount = 0;

                    // Leg counter: the "leg" is the run between two 90° turns.
                    // Any shape that introduces a 90°+ rotation breaks the leg
                    // and resets the counter. Direction-preserving shapes
                    // (pure straight, chicane, S-curve, zigzag) extend it.
                    if (pickedTurnsLeg)
                    {
                        consecutiveStraightCells = 0;
                        legIndex++;
                        currentLegFloor = JitterLegFloor(cfg, attempt, legIndex);
                    }
                    else
                        consecutiveStraightCells += ShapeTotalCells(picked.ShapeIdx);

                    frontier = new FrontierState(picked.ExitWorldCell, picked.ExitOutwardDir,
                        netRotation, lengthCells, lastShapeIdx,
                        shortCornerCount, uTurnCount, largeCurveCount,
                        diagonalCount, consecutiveWiggleCount,
                        consecutiveStraightCells, currentLegFloor);

                    // Early-abort stuck attempts: only when path has FULLY spent
                    // its length budget but rotation is < 180° (still walking
                    // away from entry). At netRot=5 (225°) the path is still
                    // physically able to curl back via short corners + closure
                    // chain — give it room. Two consecutive stuck steps → bail.
                    if (lengthCells >= (int)(cfg.TargetLengthCells * 1.1f)
                        && math.abs(netRotation) < 4)
                    {
                        stuckRotationStreak++;
                        if (stuckRotationStreak >= 2)
                        {
                            failureReason = $"stuck rotation (length={lengthCells} target={cfg.TargetLengthCells} netRot={netRotation}) — outer retry";
                            return GenerationResult.Failed(failureReason);
                        }
                    }
                    else
                    {
                        stuckRotationStreak = 0;
                    }
                }
                // Max steps without single-shape closure. Last resort: Tier-2
                // closure-chain BFS using closure-only shapes (rect-straight, 90°
                // curve, 45° transitions). Bounded depth — depth-3 = up to 3 small
                // bare pieces stitched between frontier and entry.
                if (TryCloseTier2(in entry, in frontier, ref occupancy, in terrainSnap,
                                  placedCount, out var t2EndResult))
                {
                    return t2EndResult;
                }
                failureReason = $"max steps reached ({maxSteps}) without closure";
                return GenerationResult.Failed(failureReason);
            }
            finally
            {
                if (occupancy.IsCreated) occupancy.Dispose();
            }
        }

        // ---------------- helpers ----------------

        private void EnsureSlotsBuilt()
        {
            _native?.EnsureBaked();
            if (_native == null || !_native.IsBaked) return;
            // Per-buffer guard — each persistent NativeArray is rebuilt
            // independently if it was disposed externally. The old single
            // `_slotsBuilt` flag short-circuited the whole method even when
            // a downstream Dispose left a buffer in default(NativeArray) state,
            // which surfaced as Unity's "container not assigned" job-safety
            // exception inside ExpandFrontierJob.
            if (!_slots.IsCreated)
                _slots = CandidateSlotBuilder.Build(_native.Shapes, Allocator.Persistent);
            int n = _slots.Length;
            if (!_activeSlotsBuffer.IsCreated)
                _activeSlotsBuffer = new NativeArray<CandidateSlot>(n, Allocator.Persistent);
            if (!_outputBuffer.IsCreated)
                _outputBuffer = new NativeArray<Candidate>(n, Allocator.Persistent);
            // Size to Shapes.Length (proven nonzero — same array that just
            // populated _slots above). _native.ShapeCount may be a different
            // count metric on this catalog implementation; using Shapes.Length
            // matches the index space ScoreCandidate uses (slot.ShapeIdx is in
            // [0, Shapes.Length)).
            if (!_shapeUseCounts.IsCreated)
                _shapeUseCounts = new NativeArray<short>(_native.Shapes.Length, Allocator.Persistent);
        }

        private int FindAnchorShapeIdx(in RealisticTrackGenerationConfig cfg)
        {
            // Prefer LONG_STRAIGHT (recipe pool) or closure:long-straight (authored
            // pool) for the F1-style starting straight; fall back to SINGLE_STRAIGHT
            // or the longest 2-port cardinal straight in the catalog. Picking the
            // longest (not the first) means authored-only catalogs still get a real
            // opening straight even if the long anchor wasn't registered.
            int preferred = FindShapeByName("LONG_STRAIGHT");
            if (preferred >= 0) return preferred;
            preferred = FindShapeByName(ClosureTransitionShapes.LongStraight.Id);
            if (preferred >= 0) return preferred;
            preferred = FindShapeByName("SINGLE_STRAIGHT");
            if (preferred >= 0) return preferred;
            // Last resort: longest straight-only cardinal shape.
            int bestIdx = -1;
            int bestLen = -1;
            for (int i = 0; i < _native.ShapeCount; i++)
            {
                var sd = _native.Shapes[i];
                if ((sd.KindFlags & ShapeKindFlags.HasStraight) != 0
                    && (sd.KindFlags & ShapeKindFlags.HasCurve) == 0
                    && (sd.KindFlags & ShapeKindFlags.HasDiagonal) == 0
                    && sd.PortCount == 2
                    && sd.TotalCells > bestLen)
                {
                    bestLen = sd.TotalCells;
                    bestIdx = i;
                }
            }
            return bestIdx;
        }

        private int FindShapeByName(string name)
        {
            for (int i = 0; i < _native.ShapeCount; i++)
            {
                if (_native.ShapeIds[i].Id == name) return i;
            }
            return -1;
        }

        private bool ResolveEntryAndFrontier(int anchorShapeIdx, GridPosition origin,
            TrackDirection facing, out EntryAnchor entry, out FrontierState frontier)
        {
            entry = default;
            frontier = default;
            var sd = _native.Shapes[anchorShapeIdx];
            if (sd.PortCount < 2) return false;

            int facingByte = (int)facing;
            int oppositeFacing = (facingByte + 4) & 7;
            int2 originXz = IntFromGrid(origin);

            int entryPort = -1, exitPort = -1;
            for (int p = 0; p < sd.PortCount; p++)
            {
                int gIdx = sd.PortStart + p;
                int worldOut = NativeMagnetSnap.ResolvePortOutward(_native.Ports, gIdx, facingByte);
                if (worldOut == oppositeFacing && entryPort < 0) entryPort = gIdx;
                else if (worldOut == facingByte && exitPort < 0) exitPort = gIdx;
            }
            if (entryPort < 0 || exitPort < 0) return false;

            float2 entryWorld = NativeMagnetSnap.ResolvePortWorld(_native.Ports, entryPort, originXz, facingByte);
            float2 exitWorld = NativeMagnetSnap.ResolvePortWorld(_native.Ports, exitPort, originXz, facingByte);
            entry = new EntryAnchor(entryWorld, (byte)oppositeFacing);
            frontier = new FrontierState(exitWorld, (byte)facingByte, 0,
                ShapeTotalCells(anchorShapeIdx), anchorShapeIdx);
            return true;
        }

        private bool RunForwardStep(in RealisticTrackGenerationConfig cfg,
            int attempt, int step,
            EntryAnchor entry, FrontierState frontier,
            ScoringWeights weights,
            NativeHashMap<int2, byte>.ReadOnly occupancy, TerrainSnapshot terrain,
            out Candidate picked)
        {
            picked = default;
            // Defensive: ensure all persistent buffers are alive even if
            // EnsureSlotsBuilt was bypassed by a partial init path. Cheap
            // when buffers already exist (each guard is a single IsCreated
            // check), but prevents the "container not assigned" job-safety
            // exception from leaking through to the user.
            EnsureSlotsBuilt();
            int slotMax = _slots.Length;
            if (slotMax == 0) return false;
            if (!_shapeUseCounts.IsCreated) return false;

            // Pre-filter: drop slots whose shape alone would push the running
            // length past the hard upper cap (TargetLength * 1.5, mirrored from
            // ExpandFrontierJob.ScoreCandidate). Job already returns NaN for
            // these, but we save the parallel-job cost + the NaN-skip pass.
            // Slots are rebuilt from a persistent buffer — no per-step alloc.
            float upperCap = cfg.TargetLengthCells * 1.5f;
            int activeCount = 0;
            for (int i = 0; i < slotMax; i++)
            {
                var slot = _slots[i];
                var sd = _native.Shapes[slot.ShapeIdx];
                if (frontier.LengthCellsSoFar + sd.TotalCells > upperCap) continue;
                _activeSlotsBuffer[activeCount++] = slot;
            }
            if (activeCount == 0) return false;

            var activeSlotsView = _activeSlotsBuffer.GetSubArray(0, activeCount);
            var outputView = _outputBuffer.GetSubArray(0, activeCount);

            var job = new ExpandFrontierJob
            {
                Shapes = _native.Shapes,
                Cells = _native.Cells,
                Ports = _native.Ports,
                Slots = activeSlotsView,
                Occupancy = occupancy,
                Terrain = terrain,
                Frontier = frontier,
                Entry = entry,
                Weights = weights,
                ShapeUseCounts = _shapeUseCounts,
                RngSeed = unchecked(cfg.Seed * 31 + attempt * 17),
                StepIndex = step,
                AllowDiagonals = (byte)(cfg.AllowDiagonalSweeps ? 1 : 0),
                RequireRamps = (byte)(cfg.RequireRamps ? 1 : 0),
                Output = outputView,
            };
            job.Schedule(activeCount, 16).Complete();

            // Sort candidates by descending score using parallel float[] + int[]
            // arrays (Array.Sort is in-place, no per-call delegate alloc — the
            // old List<int>.Sort(lambda) boxed a closure every step).
            if (_scoreSnap.Length < activeCount)
            {
                _scoreSnap = new float[activeCount];
                _orderSnap = new int[activeCount];
            }
            int valid = 0;
            for (int i = 0; i < activeCount; i++)
            {
                float s = outputView[i].Score;
                if (!float.IsNaN(s))
                {
                    _scoreSnap[valid] = -s;  // ascending sort on negated key = descending score
                    _orderSnap[valid] = i;
                    valid++;
                }
            }
            if (valid == 0) return false;
            Array.Sort(_scoreSnap, _orderSnap, 0, valid);

            int topK = math.min(valid, math.max(8, cfg.BeamWidth));
            for (int k = 0; k < topK; k++)
            {
                var c = outputView[_orderSnap[k]];
                if (CandidateWouldOverlapChain(c)) continue;
                picked = c;
                return true;
            }
            return false;
        }

        // Pre-placement guard. Walks the candidate shape's per-piece spine
        // samples in world space and checks each segment vs every cached chain
        // segment for centerline crossing. Skip the LAST K chain samples
        // (immediate predecessor — the candidate's anchor port is supposed to
        // mate with the frontier port, so the last chain sample sits on top
        // of the candidate's first sample by design and must be excluded).
        private bool CandidateWouldOverlapChain(in Candidate cand)
        {
            EnsureChainCenterline();
            int chainCount = _chainCenterline.Count;
            if (chainCount < 2) return false;

            var candidatePoly = BuildCandidatePolyline(cand);
            int candCount = candidatePoly.Count;
            if (candCount < 2) return false;

            int chainTailIdx = chainCount - 1;  // last chain sample sits at the frontier port

            int chainHeadIdx = 0;
            int candTailIdx = candCount - 1;
            for (int i = 0; i + 1 < candCount; i++)
            {
                int candFromStart = i;  // distance (in samples) from candidate start (= frontier)
                int candFromEnd = candTailIdx - i;  // distance from candidate end (= future closure mate target)
                Vector3 a0 = candidatePoly[i];
                Vector3 a1 = candidatePoly[i + 1];
                for (int j = 0; j + 1 < chainCount; j++)
                {
                    // Two skip conditions, both based on arc-length distance:
                    //   (a) Frontier mate: cand sample 0 sits on top of chain
                    //       tail sample. Skip pairs whose combined arc-dist
                    //       from the frontier is below the smoothing window.
                    //   (b) Closure mate: when this candidate IS the closure
                    //       shape, its exit (cand tail) mates with chain head
                    //       (anchor entry). Skip pairs near that meet too.
                    int chainFromTail = chainTailIdx - j;
                    int chainFromHead = j - chainHeadIdx;
                    if (candFromStart + chainFromTail < ChainAdjSkip) continue;
                    if (candFromEnd + chainFromHead < ChainAdjSkip) continue;

                    Vector3 b0 = _chainCenterline[j];
                    Vector3 b1 = _chainCenterline[j + 1];
                    if (Segments2DIntersect(a0, a1, b0, b1)) return true;
                    if (SqrSegmentDist2D(a0, a1, b0, b1) < CandidateMinDistSqr) return true;
                }
            }
            return false;
        }

        private const float CandidateMinDistSqr = 0.49f;  // (0.7 cells)^2 — just past lane half-width; tighter rejects too many valid layouts
        private List<Vector3> _chainCenterline;
        private int _chainCenterlinePiecesCount;
        private List<Vector3> _candidatePolylineBuf;
        private TrackChainExtractor _centerlineExtractor;

        private void EnsureChainCenterline()
        {
            _chainCenterline ??= new List<Vector3>(256);
            _centerlineExtractor ??= new TrackChainExtractor(_pieceCatalog);

            int placedCount = _placement.Placed.Count;
            if (_chainCenterlinePiecesCount == placedCount) return;
            _chainCenterline.Clear();
            var chains = _centerlineExtractor.Extract(_placement.Placed);
            for (int c = 0; c < chains.Count; c++)
            {
                var chain = chains[c];
                if (chain == null || chain.Length < 2) continue;
                var samples = CatmullRomSpline.Resample(chain, TrackPieceConstants.RibbonSampleArcStep);
                for (int s = 0; s < samples.Count; s++)
                    _chainCenterline.Add(samples[s].Position);
            }
            _chainCenterlinePiecesCount = placedCount;
        }

        // Build the world-space centerline polyline for a candidate placement
        // (a SHAPE which contains 1+ TrackShapePieces). Uses the same spine
        // samplers the chain extractor uses, so the geometry matches exactly.
        // Pooled buffer — caller must consume the returned list before another
        // BuildCandidatePolyline call.
        private List<Vector3> BuildCandidatePolyline(in Candidate cand)
        {
            _candidatePolylineBuf ??= new List<Vector3>(32);
            var poly = _candidatePolylineBuf;
            poly.Clear();
            if (!_shapeCatalog.TryGet(_native.ShapeIds[cand.ShapeIdx], out var shape) || shape == null)
                return poly;
            _centerlineExtractor ??= new TrackChainExtractor(_pieceCatalog);

            var candOrigin = GridFromInt(cand.Origin);
            var candFacing = (TrackDirection)cand.Facing;
            for (int p = 0; p < shape.Pieces.Count; p++)
            {
                var sp = shape.Pieces[p];
                if (!_pieceCatalog.TryGet(sp.PieceType, out var def)) continue;
                var (dx, dz) = TrackPieceFootprint.RotateOffset(sp.Offset.Dx, sp.Offset.Dz, candFacing);
                var pieceOrigin = new GridPosition(candOrigin.X + dx, candOrigin.Y + dz);
                var pieceFacing = (TrackDirection)(((byte)sp.LocalFacing + (byte)candFacing) & 7);
                var spine = _centerlineExtractor.BuildWorldSpine(def, pieceOrigin, pieceFacing, 1f);
                if (spine == null) continue;
                for (int s = 0; s < spine.Length; s++) poly.Add(spine[s].WorldPos);
            }
            return poly;
        }

        private static float SqrSegmentDist2D(Vector3 a0, Vector3 a1, Vector3 b0, Vector3 b1)
        {
            // Closest approach between two 2D line segments. Used to reject
            // candidates that come too close to existing chain segments even
            // when they don't strictly cross.
            float d = math.min(SqrSegPoint2D(a0, a1, b0), SqrSegPoint2D(a0, a1, b1));
            d = math.min(d, SqrSegPoint2D(b0, b1, a0));
            d = math.min(d, SqrSegPoint2D(b0, b1, a1));
            return d;
        }

        private static float SqrSegPoint2D(Vector3 a, Vector3 b, Vector3 p)
        {
            float dx = b.x - a.x, dz = b.z - a.z;
            float len2 = dx * dx + dz * dz;
            if (len2 < 1e-6f) { float dxp = p.x - a.x, dzp = p.z - a.z; return dxp * dxp + dzp * dzp; }
            float t = ((p.x - a.x) * dx + (p.z - a.z) * dz) / len2;
            t = math.clamp(t, 0f, 1f);
            float qx = a.x + t * dx, qz = a.z + t * dz;
            float ex = p.x - qx, ez = p.z - qz;
            return ex * ex + ez * ez;
        }

        private bool TryCloseTier1(in EntryAnchor entry, in FrontierState frontier,
            in NativeHashMap<int2, byte> occupancy, in TerrainSnapshot terrain,
            int lastShapeIdx, int bodyPiecesPlaced, out GenerationResult result)
        {
            result = default;
            // Walk every shape × every (anchorPort, exitPort) pair and look for one
            // that mates anchorPort with frontier and exitPort with entry.
            for (int s = 0; s < _native.ShapeCount; s++)
            {
                var sd = _native.Shapes[s];
                if (sd.PortCount < 2) continue;
                for (int aLocal = 0; aLocal < sd.PortCount; aLocal++)
                {
                    int anchorGlobal = sd.PortStart + aLocal;
                    var ap = _native.Ports[anchorGlobal];
                    int desiredFacing = (frontier.OutwardDir + 4 - ap.ShapeLocalSide) & 7;
                    if ((desiredFacing & 1) != 0) continue;
                    if (!NativeMagnetSnap.TryResolveAt(_native.Ports, anchorGlobal,
                            frontier.WorldCell, desiredFacing, out var origin)) continue;
                    if (!NativeValidators.ValidatePlacement(_native.Shapes, _native.Cells, s,
                            origin, desiredFacing, occupancy.AsReadOnly(), terrain)) continue;

                    for (int eLocal = 0; eLocal < sd.PortCount; eLocal++)
                    {
                        if (eLocal == aLocal) continue;
                        int exitGlobal = sd.PortStart + eLocal;
                        var exitWorld = NativeMagnetSnap.ResolvePortWorld(_native.Ports, exitGlobal,
                            origin, desiredFacing);
                        var d = exitWorld - entry.WorldCell;
                        if (math.lengthsq(d) > 0.25f) continue;
                        int exitOut = NativeMagnetSnap.ResolvePortOutward(_native.Ports, exitGlobal,
                            desiredFacing);
                        if (exitOut != ((entry.OutwardDir + 4) & 7)) continue;

                        // Mate found: commit and check loop closure.
                        if (!_shapeCatalog.TryGet(_native.ShapeIds[s], out var managedShape))
                            continue;
                        var commit = _shapePlacement.TryPlaceShape(
                            managedShape, GridFromInt(origin), (TrackDirection)desiredFacing);
                        if (!commit.Success) continue;
                        TrackClosureCommit(managedShape.Id, commit);

                        if (!_closedThisRun)
                        {
                            // Placement succeeded but loop wasn't detected — likely a
                            // port-quantize miss. Surface as failure so the next attempt
                            // can try a different seed offset.
                            result = GenerationResult.Failed(
                                $"Tier-1 closure placed shape '{_native.ShapeIds[s].Id}' but ClosedLoopService didn't fire");
                            return true;
                        }
                        result = GenerationResult.Ok(_closedLoopId,
                            bodyPiecesPlaced + ShapePieceCount(s) + 1 /* anchor */, _closedLoopLength);
                        return true;
                    }
                }
            }
            return false;
        }

        // ---------------- Tier-2 closure-chain BFS ----------------
        // Replaces the deleted Bezier (Tier-3) fallback. Uses small bare-variant
        // closure-only shapes registered by ClosureTransitionShapes — depth-N DFS
        // tries to chain 1..MaxClosureChain pieces between the frontier port and
        // the entry port. Bare shapes carry no walls / no kerbs by construction.
        // Bounded blowup: with ~4 closure shapes × 2 ports × 8 facings ≈ 64
        // candidates per level, depth 3 explores at most ~256k branches — most
        // pruned early by NativeValidators.

        private const int MaxClosureChainDepth = 6;
        private List<int> _closureShapeCache;

        private bool TryCloseTier2(in EntryAnchor entry, in FrontierState frontier,
            ref NativeHashMap<int2, byte> occupancy, in TerrainSnapshot terrain,
            int bodyPiecesPlaced, out GenerationResult result)
        {
            result = default;
            EnsureClosureShapeCache();
            if (_closureShapeCache == null || _closureShapeCache.Count == 0) return false;

            var chain = new List<ClosureChainStep>(MaxClosureChainDepth);
            if (!DfsClose(_closureShapeCache, (frontier.WorldCell, frontier.OutwardDir),
                          entry, ref occupancy, in terrain, MaxClosureChainDepth, chain))
            {
                return false;
            }

            // Commit chain via the managed placement service. NativeValidator already
            // approved each step; if managed disagrees we surface the divergence as
            // Failed so the outer attempt loop retries a different seed.
            int piecesPlaced = 0;
            for (int i = 0; i < chain.Count; i++)
            {
                var step = chain[i];
                if (!_shapeCatalog.TryGet(_native.ShapeIds[step.ShapeIdx], out var managedShape) || managedShape == null)
                {
                    result = GenerationResult.Failed(
                        $"Tier-2 chain step {i}: shape '{_native.ShapeIds[step.ShapeIdx].Id}' missing from managed catalog");
                    return true;
                }
                var commit = _shapePlacement.TryPlaceShape(managedShape,
                    GridFromInt(step.Origin), (TrackDirection)step.Facing);
                if (!commit.Success)
                {
                    result = GenerationResult.Failed(
                        $"Tier-2 chain step {i}: managed rejected closure shape '{_native.ShapeIds[step.ShapeIdx].Id}' that native accepted");
                    return true;
                }
                TrackClosureCommit(managedShape.Id, commit);
                piecesPlaced += managedShape.Pieces.Count;
            }

            if (!_closedThisRun)
            {
                result = GenerationResult.Failed(
                    $"Tier-2 chain ({chain.Count} pieces) placed but ClosedLoopService didn't fire");
                return true;
            }
            result = GenerationResult.Ok(_closedLoopId, bodyPiecesPlaced + piecesPlaced, _closedLoopLength);
            return true;
        }

        private void EnsureClosureShapeCache()
        {
            if (_closureShapeCache != null) return;
            _closureShapeCache = new List<int>();
            for (int s = 0; s < _native.ShapeCount; s++)
            {
                var id = _native.ShapeIds[s].Id;
                if (id != null && id.StartsWith(
                    ClosureTransitionShapes.IdPrefix,
                    StringComparison.Ordinal))
                {
                    _closureShapeCache.Add(s);
                }
            }
        }

        private readonly struct ClosureChainStep
        {
            public readonly int ShapeIdx;
            public readonly int2 Origin;
            public readonly int Facing;
            public ClosureChainStep(int shapeIdx, int2 origin, int facing)
            {
                ShapeIdx = shapeIdx;
                Origin = origin;
                Facing = facing;
            }
        }

        private bool DfsClose(List<int> closureIdx, (float2 worldCell, byte outwardDir) fr,
            EntryAnchor entry, ref NativeHashMap<int2, byte> occupancy, in TerrainSnapshot terrain,
            int depthLeft, List<ClosureChainStep> chain)
        {
            if (depthLeft <= 0) return false;
            int entryOpposite = (entry.OutwardDir + 4) & 7;
            float currentDistSqr = math.lengthsq(fr.worldCell - entry.WorldCell);

            for (int ci = 0; ci < closureIdx.Count; ci++)
            {
                int s = closureIdx[ci];
                var sd = _native.Shapes[s];
                if (sd.PortCount < 2) continue;
                for (int aLocal = 0; aLocal < sd.PortCount; aLocal++)
                {
                    int anchorGlobal = sd.PortStart + aLocal;
                    var ap = _native.Ports[anchorGlobal];
                    int desiredFacing = (fr.outwardDir + 4 - ap.ShapeLocalSide) & 7;
                    if (!NativeMagnetSnap.TryResolveAt(_native.Ports, anchorGlobal,
                            fr.worldCell, desiredFacing, out var origin)) continue;
                    if (!NativeValidators.ValidatePlacement(_native.Shapes, _native.Cells, s,
                            origin, desiredFacing, occupancy.AsReadOnly(), terrain)) continue;

                    // 1. Direct closure — does any non-anchor port mate the entry?
                    for (int eLocal = 0; eLocal < sd.PortCount; eLocal++)
                    {
                        if (eLocal == aLocal) continue;
                        int exitGlobal = sd.PortStart + eLocal;
                        var exitWorld = NativeMagnetSnap.ResolvePortWorld(_native.Ports, exitGlobal,
                            origin, desiredFacing);
                        var d = exitWorld - entry.WorldCell;
                        if (math.lengthsq(d) > 0.25f) continue;
                        int exitOut = NativeMagnetSnap.ResolvePortOutward(_native.Ports, exitGlobal,
                            desiredFacing);
                        if (exitOut != entryOpposite) continue;
                        chain.Add(new ClosureChainStep(s, origin, desiredFacing));
                        return true;
                    }

                    if (depthLeft <= 1) continue;

                    // 2. Stamp into occupancy and recurse with each non-anchor exit
                    //    as the new frontier. Distance prune: only recurse on exits
                    //    that bring us strictly closer to entry, otherwise depth-5
                    //    explodes combinatorially. Allow a 1-cell slack so detours
                    //    that route around occupancy aren't blocked entirely.
                    StampShape(s, origin, desiredFacing, ref occupancy);
                    chain.Add(new ClosureChainStep(s, origin, desiredFacing));

                    bool found = false;
                    float distanceSlack = currentDistSqr + 1.0f;
                    for (int eLocal = 0; eLocal < sd.PortCount && !found; eLocal++)
                    {
                        if (eLocal == aLocal) continue;
                        int exitGlobal = sd.PortStart + eLocal;
                        var exitWorld = NativeMagnetSnap.ResolvePortWorld(_native.Ports, exitGlobal,
                            origin, desiredFacing);
                        float nextDistSqr = math.lengthsq(exitWorld - entry.WorldCell);
                        if (nextDistSqr > distanceSlack) continue;
                        int exitOut = NativeMagnetSnap.ResolvePortOutward(_native.Ports, exitGlobal,
                            desiredFacing);
                        if (DfsClose(closureIdx, (exitWorld, (byte)exitOut), entry,
                                     ref occupancy, in terrain, depthLeft - 1, chain))
                        {
                            found = true;
                            break;
                        }
                    }
                    if (found) return true;

                    // Backtrack
                    chain.RemoveAt(chain.Count - 1);
                    UnstampShape(s, origin, desiredFacing, ref occupancy);
                }
            }
            return false;
        }

        private void StampShape(int shapeIdx, int2 origin, int facing,
            ref NativeHashMap<int2, byte> occupancy)
        {
            var sd = _native.Shapes[shapeIdx];
            for (int i = 0; i < sd.CellCount; i++)
            {
                var c = _native.Cells[sd.CellStart + i];
                int2 wc = origin + NativeMagnetSnap.RotateCellOffset(c.Dx, c.Dz, facing);
                occupancy[wc] = 1;
            }
        }

        private void UnstampShape(int shapeIdx, int2 origin, int facing,
            ref NativeHashMap<int2, byte> occupancy)
        {
            var sd = _native.Shapes[shapeIdx];
            for (int i = 0; i < sd.CellCount; i++)
            {
                var c = _native.Cells[sd.CellStart + i];
                int2 wc = origin + NativeMagnetSnap.RotateCellOffset(c.Dx, c.Dz, facing);
                occupancy.Remove(wc);
            }
        }

        // After every successful TryPlaceShape, mark the resulting piece IDs
        // as closure-pieces if the compound shape has a "closure:" prefix. The
        // export pipeline reads LastClosurePieceIds to colour those segments
        // distinctly in the Python viewer. Takes the result via duck typing
        // (ShapePlacementResult shape: bool Success + IReadOnlyList<TrackPieceId>
        // PieceIds) so we don't depend on the concrete result class name.
        private void TrackClosureCommit(TrackShapeId shapeId, ShapePlacementResult result)
        {
            if (!result.Success) return;
            if (shapeId.Id == null
                || !shapeId.Id.StartsWith(
                    ClosureTransitionShapes.IdPrefix,
                    StringComparison.Ordinal))
            {
                return;
            }
            var ids = result.PieceIds;
            if (ids == null) return;
            for (int i = 0; i < ids.Count; i++) _closurePieceIds.Add(ids[i]);
        }

        private bool CommitCandidate(in Candidate cand, ref NativeHashMap<int2, byte> occupancy,
            out IReadOnlyList<TrackPieceId> placedIds)
        {
            placedIds = Array.Empty<TrackPieceId>();
            if (!_shapeCatalog.TryGet(_native.ShapeIds[cand.ShapeIdx], out var managedShape))
                return false;
            var place = _shapePlacement.TryPlaceShape(managedShape,
                GridFromInt(cand.Origin), (TrackDirection)cand.Facing);
            if (!place.Success) return false;
            TrackClosureCommit(managedShape.Id, place);
            placedIds = place.PieceIds;
            StampShapeIntoOccupancy(cand.ShapeIdx, cand.Origin, cand.Facing, ref occupancy);
            return true;
        }

        private void UnstampShapeFromOccupancy(int shapeIdx, int2 origin, int facing,
            ref NativeHashMap<int2, byte> occupancy)
        {
            var sd = _native.Shapes[shapeIdx];
            for (int i = 0; i < sd.CellCount; i++)
            {
                var c = _native.Cells[sd.CellStart + i];
                int2 wc = origin + NativeMagnetSnap.RotateCellOffset(c.Dx, c.Dz, facing);
                occupancy.Remove(wc);
            }
        }

        private void StampShapeIntoOccupancy(int shapeIdx, int2 origin, int facing,
            ref NativeHashMap<int2, byte> occupancy)
        {
            var sd = _native.Shapes[shapeIdx];
            for (int i = 0; i < sd.CellCount; i++)
            {
                var c = _native.Cells[sd.CellStart + i];
                int2 wc = origin + NativeMagnetSnap.RotateCellOffset(c.Dx, c.Dz, facing);
                occupancy[wc] = 1;
            }
        }

        private int ShapeTotalCells(int shapeIdx) => _native.Shapes[shapeIdx].TotalCells;

        private int ShapePieceCount(int shapeIdx)
        {
            var id = _native.ShapeIds[shapeIdx];
            if (_shapeCatalog.TryGet(id, out var s) && s != null) return s.Pieces.Count;
            return 0;
        }

        private static int2 IntFromGrid(GridPosition g) => new int2(g.X, g.Y);
        private static GridPosition GridFromInt(int2 v) => new GridPosition(v.x, v.y);

        // Per-leg straight target. Hashes (seed, attempt, legIndex) into the
        // range [floor*0.7, floor*1.4] so different legs of the same loop are
        // unequal — produces varied non-rectangular shapes. The basis floor
        // auto-scales with TargetLengthCells: a 4-leg square loop has perimeter
        // ≈ 4 × leg_length, so the natural floor is target/8 (half a leg per
        // straight side). Cfg.MinMainStraightCells acts as a lower bound only.
        private static int JitterLegFloor(in RealisticTrackGenerationConfig cfg,
            int attempt, int legIndex)
        {
            int autoFloor = math.max(3, cfg.TargetLengthCells / 8);
            int basis = math.max(autoFloor, cfg.MinMainStraightCells);
            uint h = unchecked((uint)(cfg.Seed * 2654435761) ^ (uint)(attempt * 374761393)
                              ^ (uint)(legIndex * 668265263));
            h ^= h >> 13; h *= 1274126177u; h ^= h >> 16;
            float t = (h & 0xFFFF) * (1f / 65535f);
            float lerped = math.lerp(basis * 0.7f, basis * 1.4f, t);
            return math.max(3, (int)math.round(lerped));
        }

        private static ScoringWeights MakeWeights(in RealisticTrackGenerationConfig cfg)
        {
            return new ScoringWeights(
                closureBonus: 12f,
                closureLambda: 0.15f,
                lengthPenalty: 1.0f,
                targetLength: cfg.TargetLengthCells,
                sameShapePenalty: 2.5f,
                turnDensity: cfg.TurnDensity * 1.5f,
                rngNoise: 4.0f,
                maxConsecutiveStraights: cfg.MaxConsecutiveStraights);
        }

        // -------- Reflex IDisposable --------
        public override void Dispose()
        {
            if (_slots.IsCreated) _slots.Dispose();
            if (_activeSlotsBuffer.IsCreated) _activeSlotsBuffer.Dispose();
            if (_outputBuffer.IsCreated) _outputBuffer.Dispose();
            if (_shapeUseCounts.IsCreated) _shapeUseCounts.Dispose();
            base.Dispose();
        }

        // -------- closure event sink --------
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
    }
}
