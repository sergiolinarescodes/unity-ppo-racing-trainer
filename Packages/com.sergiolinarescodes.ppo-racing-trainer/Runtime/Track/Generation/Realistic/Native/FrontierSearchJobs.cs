using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace UnityPpoRacingTrainer.Core.Track.Generation.Realistic.Native
{
    // Frontier port state — the open port the next placement must mate to.
    // World cell coords (cell-space, not units), outward direction (0..7).
    // Category counters drive caps on short corners + u-turns and bonuses for
    // large curves + diagonals.
    public readonly struct FrontierState
    {
        public readonly float2 WorldCell;
        public readonly byte OutwardDir;
        public readonly int NetRotation;
        public readonly int LengthCellsSoFar;
        public readonly int LastShapeIdx;
        public readonly int ShortCornerCount;
        public readonly int UTurnCount;
        public readonly int LargeCurveCount;
        public readonly int DiagonalCount;
        public readonly int ConsecutiveWiggleCount;
        // Cells of straight running since the last 90° turn. Resets on every
        // turn (corner / U-turn / diagonal). The heuristic uses this to enforce
        // an F1 "main straight" of MinMainStraightCells before a corner, and
        // to push for a long back-straight after the corner too.
        public readonly int ConsecutiveStraightCells;
        public readonly int MinMainStraightCells;

        public FrontierState(float2 worldCell, byte outwardDir, int netRotation,
                             int lengthCellsSoFar, int lastShapeIdx,
                             int shortCornerCount = 0, int uTurnCount = 0,
                             int largeCurveCount = 0, int diagonalCount = 0,
                             int consecutiveWiggleCount = 0,
                             int consecutiveStraightCells = 0,
                             int minMainStraightCells = 7)
        {
            WorldCell = worldCell;
            OutwardDir = outwardDir;
            NetRotation = netRotation;
            LengthCellsSoFar = lengthCellsSoFar;
            LastShapeIdx = lastShapeIdx;
            ShortCornerCount = shortCornerCount;
            UTurnCount = uTurnCount;
            LargeCurveCount = largeCurveCount;
            DiagonalCount = diagonalCount;
            ConsecutiveWiggleCount = consecutiveWiggleCount;
            ConsecutiveStraightCells = consecutiveStraightCells;
            MinMainStraightCells = minMainStraightCells;
        }
    }

    // The entry port of the very first piece placed in this attempt — the closure
    // target. To close the loop, a candidate's exit port must land at this world cell
    // with outward direction = (OutwardDir + 4) & 7 (i.e. opposite of the entry port's
    // outward direction, so the two ports kiss).
    public readonly struct EntryAnchor
    {
        public readonly float2 WorldCell;
        public readonly byte OutwardDir;

        public EntryAnchor(float2 worldCell, byte outwardDir)
        {
            WorldCell = worldCell;
            OutwardDir = outwardDir;
        }
    }

    // Tunable weights for the candidate scoring heuristic. All in [0, 1] except
    // Lambda, which is the negative-exponential rate for closure-proximity bonus.
    public readonly struct ScoringWeights
    {
        public readonly float ClosureBonus;
        public readonly float ClosureLambda;
        public readonly float LengthPenalty;
        public readonly float TargetLength;
        public readonly float SameShapePenalty;
        public readonly float TurnDensity;
        public readonly float RngNoise;
        public readonly int MaxConsecutiveStraights;

        public ScoringWeights(float closureBonus, float closureLambda,
                              float lengthPenalty, float targetLength,
                              float sameShapePenalty, float turnDensity,
                              float rngNoise, int maxConsecutiveStraights)
        {
            ClosureBonus = closureBonus;
            ClosureLambda = closureLambda;
            LengthPenalty = lengthPenalty;
            TargetLength = targetLength;
            SameShapePenalty = sameShapePenalty;
            TurnDensity = turnDensity;
            RngNoise = rngNoise;
            MaxConsecutiveStraights = maxConsecutiveStraights;
        }
    }

    // One candidate placement: which shape, where, with what facing, and the
    // resulting frontier (the exit port world pose). Score is NaN when invalid.
    public struct Candidate
    {
        public int ShapeIdx;
        public int AnchorPortGlobalIdx;
        public int ExitPortGlobalIdx;
        public int2 Origin;
        public byte Facing;
        public byte ClosureFlag;     // 0=none, 1=closure-eligible (tier 1)
        public ushort _pad;
        public float2 ExitWorldCell;
        public byte ExitOutwardDir;
        public byte _pad2;
        public ushort _pad3;
        public int NetRotationAfter;
        public int LengthAfter;
        public float Score;
    }

    // Maps the parallel work index to (shapeIdx, anchorPortGlobalIdx). Built once
    // per session because catalog is immutable.
    public readonly struct CandidateSlot
    {
        public readonly int ShapeIdx;
        public readonly int AnchorPortGlobalIdx;

        public CandidateSlot(int shapeIdx, int anchorPortGlobalIdx)
        {
            ShapeIdx = shapeIdx;
            AnchorPortGlobalIdx = anchorPortGlobalIdx;
        }
    }

    [BurstCompile]
    public struct ExpandFrontierJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<ShapeDescriptor> Shapes;
        [ReadOnly] public NativeArray<PieceCell> Cells;
        [ReadOnly] public NativeArray<PortDescriptor> Ports;
        [ReadOnly] public NativeArray<CandidateSlot> Slots;
        [ReadOnly] public NativeHashMap<int2, byte>.ReadOnly Occupancy;
        [ReadOnly] public TerrainSnapshot Terrain;
        [ReadOnly] public FrontierState Frontier;
        [ReadOnly] public EntryAnchor Entry;
        [ReadOnly] public ScoringWeights Weights;
        // Per-shape use counts for THIS attempt. Drives a progressive penalty
        // in ScoreCandidate so loops pull from many distinct shapes instead of
        // hammering 2-3 favourites. Indexed by ShapeIdx.
        [ReadOnly] public NativeArray<short> ShapeUseCounts;
        [ReadOnly] public int RngSeed;
        [ReadOnly] public int StepIndex;
        [ReadOnly] public byte AllowDiagonals;
        [ReadOnly] public byte RequireRamps;

        [WriteOnly] public NativeArray<Candidate> Output;

        public void Execute(int idx)
        {
            var slot = Slots[idx];
            var sd = Shapes[slot.ShapeIdx];
            var cand = new Candidate
            {
                ShapeIdx = slot.ShapeIdx,
                AnchorPortGlobalIdx = slot.AnchorPortGlobalIdx,
                Score = float.NaN,
            };

            if ((AllowDiagonals == 0) && (sd.KindFlags & ShapeKindFlags.HasDiagonal) != 0)
            {
                Output[idx] = cand; return;
            }

            int desired = (Frontier.OutwardDir + 4) & 7;
            var anchorPort = Ports[slot.AnchorPortGlobalIdx];
            int facing = (desired - anchorPort.ShapeLocalSide) & 7;
            // Cardinal output only — diagonal facings are not supported by the placement pipeline.
            if ((facing & 1) != 0) { Output[idx] = cand; return; }

            float2 shapeRot = NativeMagnetSnap.RotateAroundHalf(
                new float2(anchorPort.Shx, anchorPort.Shz), facing);
            float wx = Frontier.WorldCell.x - 0.5f - shapeRot.x;
            float wz = Frontier.WorldCell.y - 0.5f - shapeRot.y;
            int2 origin = new int2((int)math.round(wx), (int)math.round(wz));

            if (!NativeValidators.ValidatePlacement(
                    Shapes, Cells, slot.ShapeIdx, origin, facing, Occupancy, Terrain))
            {
                Output[idx] = cand; return;
            }

            // Pick the exit port: any boundary port of this shape that's NOT the anchor.
            // For 2-port shapes (the dominant case) this is unambiguous. For multi-port
            // shapes pick the one whose world outward direction is closest to the
            // forward heading (= original frontier outward, since we mate inward).
            int exitGlobalIdx = -1;
            float bestForwardScore = float.NegativeInfinity;
            for (int p = 0; p < sd.PortCount; p++)
            {
                int globalIdx = sd.PortStart + p;
                if (globalIdx == slot.AnchorPortGlobalIdx) continue;
                int worldOut = (Ports[globalIdx].ShapeLocalSide + facing) & 7;
                float forward = ForwardAlignment(worldOut, Frontier.OutwardDir);
                if (forward > bestForwardScore)
                {
                    bestForwardScore = forward;
                    exitGlobalIdx = globalIdx;
                }
            }
            if (exitGlobalIdx < 0)
            {
                // Single-port shape (terminal) — not usable as a forward step.
                Output[idx] = cand; return;
            }

            // Compute exit pose.
            var exitPort = Ports[exitGlobalIdx];
            float2 exitShapeRot = NativeMagnetSnap.RotateAroundHalf(
                new float2(exitPort.Shx, exitPort.Shz), facing);
            float2 exitWorld = new float2(origin.x + 0.5f + exitShapeRot.x,
                                          origin.y + 0.5f + exitShapeRot.y);
            int exitOutward = (exitPort.ShapeLocalSide + facing) & 7;

            // Net rotation update: signed difference between exit outward and original
            // frontier outward (positive = right turn, negative = left).
            int rotDelta = SignedRotationDelta(Frontier.OutwardDir, (byte)exitOutward);
            int netRotAfter = Frontier.NetRotation + rotDelta;
            int lengthAfter = Frontier.LengthCellsSoFar + sd.TotalCells;

            // Score.
            float score = ScoreCandidate(slot.ShapeIdx, sd, exitWorld, exitOutward,
                                          netRotAfter, lengthAfter, rotDelta);

            // Closure flag (tier 1): only allow loop closure when we've actually
            // built a meaningful track. Early closure produces the tiny ovals the
            // user complained about — gate by phase + tight distance + cardinal
            // outward + ~360° rotation.
            byte closureFlag = 0;
            float2 dEntry = exitWorld - Entry.WorldCell;
            float distSqr = math.lengthsq(dEntry);
            int desiredOutwardForClose = (Entry.OutwardDir + 4) & 7;
            float phase = math.min(1f, lengthAfter / math.max(1f, Weights.TargetLength));
            int rotMod8 = ((netRotAfter % 8) + 8) % 8;
            bool rotComplete = rotMod8 <= 1 || rotMod8 >= 7;
            if (phase >= 0.6f && distSqr < 1.0f && exitOutward == desiredOutwardForClose
                && rotComplete)
            {
                closureFlag = 1;
                score += Weights.ClosureBonus * 5f;
            }

            cand.Origin = origin;
            cand.Facing = (byte)facing;
            cand.ExitPortGlobalIdx = exitGlobalIdx;
            cand.ExitWorldCell = exitWorld;
            cand.ExitOutwardDir = (byte)exitOutward;
            cand.NetRotationAfter = netRotAfter;
            cand.LengthAfter = lengthAfter;
            cand.Score = score;
            cand.ClosureFlag = closureFlag;
            Output[idx] = cand;
        }

        private float ScoreCandidate(int shapeIdx, ShapeDescriptor sd,
                                     float2 exitWorld, int exitOutward,
                                     int netRotAfter, int lengthAfter,
                                     int rotDelta)
        {
            float s = 0f;
            float phase = math.min(1f, lengthAfter / math.max(1f, Weights.TargetLength));

            // Forward-progress reward while we haven't reached target length.
            // Square-root scaling so a 4-cell shape doesn't drown out variety —
            // every shape gets some forward credit, but bigger shapes only modestly
            // win on raw length.
            float lengthGain = lengthAfter - Frontier.LengthCellsSoFar;
            s += (1f - phase) * math.sqrt(math.max(0f, lengthGain)) * 0.6f;

            // Penalize length over the upper bound (1.5 * target). Hard cap.
            float upper = Weights.TargetLength * 1.5f;
            if (lengthAfter > upper)
                s -= (lengthAfter - upper) * 2f;

            // Diversity scoring: progressive penalty by per-attempt use count.
            // 1st reuse modest, 2nd big, 3rd+ severe — pulls many distinct
            // shapes into the loop instead of hammering 2-3 favourites.
            // Pure-straights (LONG_STRAIGHT) get a big discount: we WANT
            // consecutive straights to build the main straight.
            short useCount = ShapeUseCounts[shapeIdx];
            if (useCount > 0)
            {
                float reusePenalty;
                if (useCount == 1) reusePenalty = Weights.SameShapePenalty;
                else if (useCount == 2) reusePenalty = Weights.SameShapePenalty * 2.5f;
                else reusePenalty = Weights.SameShapePenalty * 2.5f
                                    + (useCount - 2) * Weights.SameShapePenalty * 1.5f;
                if ((sd.KindFlags & ShapeKindFlags.IsPureStraight) != 0) reusePenalty *= 0.6f;
                s -= reusePenalty;
            }

            // ------- Straight-run enforcement -------
            // F1 main / back straights drive most of the loop length. The
            // "leg" is the run between two 90° turns. Detect leg breaks by
            // ROTATION DELTA, not shape category — LONG_RIGHT, LOOP_QUARTER
            // and many "large curve" shapes are 90° turns (rotDelta==±2)
            // and were leaking through the old flag-based check, letting
            // the path pile up corners without ever building a straight.
            // Conversely chicanes / S-curves / zigzags have rotDelta==0
            // and continue the leg even though they're "wiggle" shapes —
            // they add lateral displacement without breaking direction.
            int straightFloor = math.max(3, Frontier.MinMainStraightCells);
            int currentStraight = Frontier.ConsecutiveStraightCells;
            int absRotDelta = math.abs(rotDelta);
            bool turnsTheLeg = absRotDelta >= 2;        // 90° or more
            bool extendsTheLeg = absRotDelta == 0;       // pure straight, S-curve, chicane

            // Soft band: floor → 1.4 × floor. Below floor, strongly prefer
            // straights. Above 1.4 × floor, strongly prefer turning so legs
            // stay roughly equal length and loops become squarer (was the
            // root cause of the user's "20×7" narrow horizontal layouts).
            int straightCeiling = (int)(straightFloor * 1.4f);
            if (currentStraight < straightFloor)
            {
                if (extendsTheLeg)
                    s += 4f * (1f - (float)currentStraight / straightFloor);
                else if (turnsTheLeg)
                    s -= 5f * (1f - (float)currentStraight / straightFloor);
            }
            else if (currentStraight >= straightFloor && phase < 0.85f)
            {
                if (turnsTheLeg)
                    s += 5f;  // strong push to corner now — keeps legs equal
                else if (extendsTheLeg && currentStraight >= straightCeiling)
                    s -= 4f * ((float)(currentStraight - straightCeiling) / straightFloor + 1f);
            }

            // ------- Shape-category preferences -------
            // Drive F1 character: prefer LARGE curves + diagonals; soft-cap
            // short corners (3-5) and U-turns (≤1); break up wiggle runs to
            // prevent chaotic zigzag stretches. Penalties dampen in late phase
            // so closure isn't blocked by the caps.
            bool isShortCorner = (sd.KindFlags & ShapeKindFlags.IsShortCorner) != 0;
            bool isUTurn = (sd.KindFlags & ShapeKindFlags.IsUTurn) != 0;
            bool isLargeCurve = (sd.KindFlags & ShapeKindFlags.IsLargeCurve) != 0;
            bool isDiagonal = (sd.KindFlags & ShapeKindFlags.IsDiagonalShape) != 0;
            bool isPureStraight = (sd.KindFlags & ShapeKindFlags.IsPureStraight) != 0;
            bool isWiggleish = isLargeCurve || isUTurn;
            float capStrength = math.max(0f, 1f - phase);  // 1 early, 0 at target

            // Variety-rich shape bonuses. Diagonals get the strongest pull
            // because the user's reference layout had only a single diagonal
            // section. Large flowing curves get a steady mid-phase boost.
            if (isLargeCurve && phase > 0.2f && phase < 0.9f)
                s += Weights.TurnDensity * 1.4f;
            if (isDiagonal && phase > 0.15f && phase < 0.95f)
                s += Weights.TurnDensity * 2.0f;

            // Soft cap on short corners (target 3-5 per loop).
            if (isShortCorner)
            {
                int newCount = Frontier.ShortCornerCount + 1;
                if (newCount > 5) s -= 4f * (newCount - 5) * capStrength;
                else if (newCount > 3) s -= 0.5f * (newCount - 3) * capStrength;
            }

            // Soft cap on U-turns (≤1 per loop).
            if (isUTurn)
            {
                int newCount = Frontier.UTurnCount + 1;
                if (newCount > 1) s -= 6f * (newCount - 1) * capStrength;
                else s += 0.5f;  // mild bonus for the first U-turn (drama)
            }

            // Anti-zigzag: penalty for chaining wiggle-class shapes back-to-back.
            if (isWiggleish)
            {
                int newWiggle = Frontier.ConsecutiveWiggleCount + 1;
                if (newWiggle >= 2) s -= 1.5f * newWiggle * capStrength;
            }
            else if (isPureStraight)
            {
                // Reward straights when wiggles have been chained — breathing room.
                s += math.min(Frontier.ConsecutiveWiggleCount, 3) * 0.8f;
            }

            // Closure pull: linear (not quadratic) attraction toward entry once
            // we're past the half-target mark. With Lambda small, the exp term
            // gives meaningful gradient up to ~target-radius cells.
            float distToEntry = math.length(exitWorld - Entry.WorldCell);
            float pullStrength = math.max(0f, phase - 0.3f) / 0.7f;  // 0 below 30%, ramps to 1 at target
            s += pullStrength * Weights.ClosureBonus
                 * math.exp(-Weights.ClosureLambda * distToEntry);

            // Hard radius cap: once we exceed ~half the target-length, paths
            // beyond this radius from entry incur a steep linear penalty. Without
            // this, greedy paths wander to grid edges and dead-end.
            float maxLoopRadius = math.max(8f, Weights.TargetLength * 0.35f);
            if (distToEntry > maxLoopRadius)
                s -= (distToEntry - maxLoopRadius) * 2.5f;

            // Net-rotation pressure: reward consistent right-handed rotation
            // toward 8 (one full clockwise loop) — proportional to phase so the
            // path doesn't try to complete rotation before building length.
            int rotClamped = math.clamp(netRotAfter, -8, 8);
            s += rotClamped * 0.6f * phase;
            int absRot = math.abs(netRotAfter);
            if (absRot > 8) s -= (absRot - 8) * 1.5f;

            // Rotation pacing: a closed loop needs |netRot|≈8 by phase 1.0.
            // Expected pace = 8 × phase. Both DEFICIT and SURPLUS are bad —
            // under-rotation dead-ends at netRot<6 with length spent; over-
            // rotation curls into self before length builds. Soft kick-in
            // at phase 0.15 so the opening straight isn't punished.
            if (phase >= 0.15f)
            {
                float expectedRot = 8f * phase;
                float rotMag = math.abs(netRotAfter);
                if (rotMag < expectedRot)
                {
                    float deficit = expectedRot - rotMag;
                    s -= deficit * 1.8f * phase;
                }
                else if (rotMag > expectedRot + 3f)
                {
                    float surplus = rotMag - expectedRot - 3f;
                    s -= surplus * 1.5f;
                }
            }

            // Once rotation is mostly complete AND length is sufficient, pull
            // toward entry. Mild weight (1.0) so the path doesn't collapse to
            // shortest-rectangle layouts — Tier-2 closure-chain handles the
            // final stitch even when the body wanders.
            if (absRot >= 6 && phase >= 0.7f)
                s -= distToEntry * 1.0f;

            // Closure-rotation bias: when length is near target but rotation
            // is incomplete (|netRot| < 6), strongly reward shapes that add
            // rotation (short corners, U-turns). Earlier kick-in (0.4 not 0.6)
            // so curl starts before length budget is spent. Higher multipliers
            // (was 0.5/0.7 → now 0.8/1.0) so closure-shaped candidates beat
            // the straight-line incumbent.
            if (phase >= 0.4f && absRot < 6 && absRotDelta >= 2)
            {
                float rotDeficit = 6f - absRot;
                if (isShortCorner) s += Weights.ClosureBonus * 0.8f * rotDeficit;
                if (isUTurn) s += Weights.ClosureBonus * 1.0f * rotDeficit;
            }

            // RNG noise for seed-driven variety.
            if (Weights.RngNoise > 0f)
            {
                uint h = HashState(RngSeed, StepIndex, shapeIdx, sd.CellStart);
                float u = (h & 0xFFFF) * (1f / 65535f);
                s += Weights.RngNoise * (u - 0.5f);
            }

            return s;
        }

        private static int SignedRotationDelta(byte beforeDir, byte afterDir)
        {
            int diff = (afterDir - beforeDir) & 7;
            // Map to signed range [-4, 4)
            if (diff > 4) diff -= 8;
            return diff;
        }

        private static float ForwardAlignment(int worldOut, byte frontierOut)
        {
            // Encourage exits that continue forward (perpendicular = 0, opposite = -1, same = 1)
            int delta = (worldOut - frontierOut) & 7;
            if (delta > 4) delta -= 8;
            // Same dir = 0 → 1.0, ±2 (90°) → 0.5, ±4 (180°) → -1.0
            float radians = delta * (math.PI / 4f);
            return math.cos(radians);
        }

        private static uint HashState(int seed, int step, int shape, int cellStart)
        {
            uint h = (uint)seed;
            h ^= (uint)step * 2654435761u;
            h ^= (uint)shape * 374761393u;
            h ^= (uint)cellStart * 668265263u;
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return h;
        }
    }

    // Helper to build the per-session CandidateSlot mapping. Catalog is immutable
    // so this is built once and reused across all generation attempts.
    public static class CandidateSlotBuilder
    {
        public static NativeArray<CandidateSlot> Build(
            in NativeArray<ShapeDescriptor> shapes, Allocator allocator)
        {
            int total = 0;
            for (int s = 0; s < shapes.Length; s++) total += shapes[s].PortCount;
            var arr = new NativeArray<CandidateSlot>(total, allocator);
            int outIdx = 0;
            for (int s = 0; s < shapes.Length; s++)
            {
                var sd = shapes[s];
                for (int p = 0; p < sd.PortCount; p++)
                {
                    arr[outIdx++] = new CandidateSlot(s, sd.PortStart + p);
                }
            }
            return arr;
        }
    }

    // Selection: scan candidates and pick one. PickBest is strict argmax.
    // PickWeighted samples from the top-K by score using a seeded uniform draw —
    // produces seed-driven variety while still preferring high-scoring moves.
    // Not Burst-decorated (managed call from generator main thread; Burst would
    // reject struct-by-value).
    public static class TopCandidate
    {
        public static bool PickBest(in NativeArray<Candidate> candidates,
                                    out int bestIdx, out float bestScore)
        {
            bestIdx = -1;
            bestScore = float.NegativeInfinity;
            for (int i = 0; i < candidates.Length; i++)
            {
                var c = candidates[i];
                if (float.IsNaN(c.Score)) continue;
                if (c.Score > bestScore)
                {
                    bestScore = c.Score;
                    bestIdx = i;
                }
            }
            return bestIdx >= 0;
        }

        // Picks one of the top-K candidates by score, using uniform draw `t` in [0, 1).
        // K-clamped to the number of valid candidates. If a closure-flagged candidate
        // exists, it always wins (we never want to pass on a clean loop close).
        public static bool PickWeighted(in NativeArray<Candidate> candidates,
                                         int topK, float t,
                                         out int chosenIdx, out float chosenScore)
        {
            chosenIdx = -1;
            chosenScore = float.NegativeInfinity;

            // Closure short-circuit: any valid closure candidate wins outright.
            int closureBest = -1;
            float closureBestScore = float.NegativeInfinity;
            int validCount = 0;
            for (int i = 0; i < candidates.Length; i++)
            {
                var c = candidates[i];
                if (float.IsNaN(c.Score)) continue;
                validCount++;
                if (c.ClosureFlag != 0 && c.Score > closureBestScore)
                {
                    closureBestScore = c.Score;
                    closureBest = i;
                }
            }
            if (closureBest >= 0)
            {
                chosenIdx = closureBest;
                chosenScore = closureBestScore;
                return true;
            }
            if (validCount == 0) return false;

            // Build the top-K by score. Insertion sort, K is small (≤8 typically).
            int k = math.min(topK, validCount);
            if (k <= 0) return false;
            var topIdx = new NativeArray<int>(k, Allocator.Temp);
            var topScore = new NativeArray<float>(k, Allocator.Temp);
            for (int i = 0; i < k; i++) { topIdx[i] = -1; topScore[i] = float.NegativeInfinity; }

            for (int i = 0; i < candidates.Length; i++)
            {
                var c = candidates[i];
                if (float.IsNaN(c.Score)) continue;
                // Find insertion position.
                int pos = -1;
                for (int j = 0; j < k; j++)
                {
                    if (c.Score > topScore[j]) { pos = j; break; }
                }
                if (pos < 0) continue;
                // Shift right.
                for (int j = k - 1; j > pos; j--)
                {
                    topIdx[j] = topIdx[j - 1];
                    topScore[j] = topScore[j - 1];
                }
                topIdx[pos] = i;
                topScore[pos] = c.Score;
            }

            // Pick uniformly from the populated top-K slots.
            int populated = 0;
            for (int j = 0; j < k; j++) if (topIdx[j] >= 0) populated++;
            if (populated == 0) { topIdx.Dispose(); topScore.Dispose(); return false; }
            int pick = (int)math.floor(math.clamp(t, 0f, 0.9999f) * populated);
            chosenIdx = topIdx[pick];
            chosenScore = topScore[pick];
            topIdx.Dispose();
            topScore.Dispose();
            return true;
        }
    }
}
