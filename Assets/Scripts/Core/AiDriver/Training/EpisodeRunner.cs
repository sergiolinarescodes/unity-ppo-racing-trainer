using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.AiDriver.Loop;
using UnityPpoRacingTrainer.Core.AiDriver.Physics;
using UnityPpoRacingTrainer.Core.AiDriver.Race;
using UnityPpoRacingTrainer.Core.Track.Loop;
using Unidad.Core.Abstractions;
using Unidad.Core.EventBus;
using Unidad.Core.Systems;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.AiDriver.Training
{
    /// <summary>
    /// Per-car episode bookkeeper plus the primary potential-based reward
    /// source. Combines forward arc progress, lateral-offset penalty,
    /// steering smoothness, and an array of terminal bonuses / penalties to
    /// shape policy behaviour. Runs alongside the pluggable
    /// <see cref="RewardShaper"/> when extra physics signals are wired in.
    /// </summary>
    internal sealed class EpisodeRunner : SystemServiceBase, IEpisodeRewardSource
    {
        /// <summary>
        /// Maximum decision-step budget for one episode. At 5 sim ticks per
        /// decision and a 50 Hz fixed step that is ~1200 s of sim time per
        /// episode — wide enough to comfortably bank 1-2 laps even on long
        /// procedural loops at the canonical acc/brake pace, and to absorb
        /// temporary policy regressions without immediately classifying
        /// the lap as Failure_Timeout.
        /// UP: episodes get more wall-clock headroom; multi-lap stints become
        /// possible; longer learning windows but slower trainer throughput.
        /// DOWN: episodes hard-cap sooner; the policy must learn closure
        /// faster but very long circuits may not fit.
        /// </summary>
        public int MaxStepsPerEpisode { get; set; } = 12000;

        /// <summary>
        /// Maximum sustained off-track time before the episode ends with
        /// <see cref="EpisodeEndReason.Failure_OffTrack"/>, in seconds.
        /// UP: cars get more grace to recover from a wide exit; encourages
        /// chancy lines and shortcuts.
        /// DOWN: any off-track excursion is quickly terminal; punishes
        /// shortcutting through infields hard.
        /// </summary>
        public float OffTrackTimeoutSec { get; set; } = 0.5f;

        private readonly ICarSimulationService _carSim;
        private readonly ITrackQueryService _trackQuery;
        private readonly IClosedLoopService _loop;
        private readonly ITimeProvider _time;
        // Optional. When non-null and IsRaceScoped, EpisodeRunner stops returning
        // per-car terminals (wreck/wall-cap/off-track/step-cap) and instead
        // publishes DriverEliminatedEvent / DriverFinishedRaceEvent. The car's
        // PPO episode stays open until the coordinator signals race-end via
        // ShouldEndCarEpisode.
        private readonly IRaceCoordinator _coord;

        private readonly Dictionary<CarId, EpisodeState> _episodes = new();
        private bool _loopOpenAbortPending;

        private readonly Dictionary<EpisodeEndReason, int> _endReasonCounts = new();
        private int _totalEnded;
        private int _successesInWindow;
        private const int LogEveryNEpisodes = 100;

        public EpisodeRunner(
            IEventBus eventBus,
            ICarSimulationService carSim,
            ITrackQueryService trackQuery,
            IClosedLoopService loop,
            ITimeProvider time,
            IRaceCoordinator coord = null) : base(eventBus)
        {
            _carSim = carSim;
            _trackQuery = trackQuery;
            _loop = loop;
            _time = time;
            _coord = coord;

            Subscribe<CarOffTrackEvent>(OnOffTrack);
            Subscribe<CarBackOnTrackEvent>(OnBackOnTrack);
            Subscribe<CarHitWallEvent>(OnHitWall);
            Subscribe<CarOnKerbEvent>(evt => SetKerb(evt.Id, true));
            Subscribe<CarOffKerbEvent>(evt => SetKerb(evt.Id, false));
            Subscribe<LoopOpenedEvent>(_ => _loopOpenAbortPending = true);
        }

        // Cached at race-start by the coordinator into its per-race latch;
        // EpisodeRunner inherits that latching by routing through the coord.
        private bool IsRaceScoped => _coord != null && _coord.IsRaceScoped;

        private void SetKerb(CarId id, bool on)
        {
            if (_episodes.TryGetValue(id, out var ep))
            {
                ep.IsOnKerb = on;
                _episodes[id] = ep;
            }
        }

        public void OnEpisodeBegin(CarId carId)
        {
            float now = _time.Time;
            float startArc = 0f;
            Vector3 startWorldPos = Vector3.zero;
            int startMicro = 0;
            int startMacro = 0;
            int startMask = 1 << 0;
            int nextRequired = 1;
            if (_carSim.TryGetState(carId, out var state) && _trackQuery.HasLoop
                && _loop.TryGetCurrentLoop(out var loopAtStart) && loopAtStart.TotalLength > 0f)
            {
                var proj = _trackQuery.Project(state.Position, state.LastAnchorIndex);
                startArc = proj.ArcLengthAlong;
                startWorldPos = state.Position;
                startMicro = loopAtStart.Sectors.MicroSectorOf(startArc);
                startMacro = loopAtStart.Sectors.MacroSectorOf(startMicro);
                startMask = 1 << startMicro;
                int microCount = Mathf.Max(loopAtStart.Sectors.MicroCount, 1);
                nextRequired = (startMicro + 1) % microCount;
            }
            _episodes[carId] = new EpisodeState
            {
                StartTime = now,
                LastTickTime = now,
                LapStartTime = now,
                LapStartReward = 0f,
                LapStartWallHits = 0,
                PrevArc = startArc,
                PrevWorldPos = startWorldPos,
                CumulativeForwardArc = 0f,
                IsOffTrack = false,
                OffTrackTimer = 0f,
                Steps = 0,
                LapsCompleted = 0,
                CumulativeReward = 0f,
                PrevSteer = 0f,
                PrevThrottle = 0f,
                HasPrevAction = false,
                WallHitPending = false,
                IsOnKerb = false,
                NextRequiredMicro = nextRequired,
                LastSeenMicro = startMicro,
                MicroSectorsHitMask = startMask,
                CurrentMacro = startMacro,
                LapValidThisGo = true,
            };
            _loopOpenAbortPending = false;
        }

        public StepResult PostStep(CarId carId)
        {
            if (!_episodes.TryGetValue(carId, out var ep)) return StepResult.None;

            if (_loopOpenAbortPending)
            {
                PublishEnd(carId, ep, EpisodeEndReason.Aborted);
                return new StepResult(0f, EpisodeEndReason.Aborted);
            }

            // Race-scoped flow: when the coordinator declares race-end, EVERY
            // tracked driver's PPO episode ends on its next decision tick,
            // carrying the previously-latched RaceResolvedReason (Success for
            // finishers, Failure_* for eliminated, default Success if neither
            // ever fired — e.g. the AbortRace path).
            if (_coord != null && _coord.ShouldEndCarEpisode(carId))
            {
                var endReason = ep.RaceResolvedReason ?? EpisodeEndReason.Success;
                PublishEnd(carId, ep, endReason);
                _coord.AcknowledgeEpisodeEnd(carId);
                return new StepResult(0f, endReason);
            }

            // Race-scoped tail: a driver who already finished the lap target
            // or was eliminated keeps its agent alive (no EndEpisode) but
            // contributes a zero-reward, zero-event idle stream until the
            // race-end signal arrives. Suppresses double-counting the
            // resolution penalty/bonus while the slowest driver is still
            // racing.
            if (ep.RaceResolvedReason.HasValue)
            {
                return new StepResult(0f, null);
            }

            float now = _time.Time;
            float dt = Mathf.Max(0f, now - ep.LastTickTime);
            ep.LastTickTime = now;
            ep.Steps++;
            // Dashboard breadcrumb cadence. Was 25 → ~2 Hz at typical decision
            // period, which produced ~100 MB/h of jsonl per env across 8 envs
            // and contributed to the long-run host OOM. Bumped to 10000 (user
            // request): episode_end / lap_complete / wall_hit still emit
            // event-driven so the dashboard keeps actionable signal; only the
            // dense position breadcrumb stream is throttled.
            const int StepSampleInterval = 10000;
            bool sampleThisStep = (ep.Steps % StepSampleInterval) == 0;

            if (ep.IsOffTrack) ep.OffTrackTimer += dt;
            else ep.OffTrackTimer = 0f;

            if (!_carSim.TryGetState(carId, out var state))
            {
                _episodes[carId] = ep;
                return StepResult.None;
            }

            float deltaArc = 0f;
            float lateralOffset = 0f;
            float halfWidth = 0f;
            bool isOffTrack = ep.IsOffTrack;
            bool lapJustCompleted = false;
            float lapElapsedSec = 0f;
            bool sectorJustHit = false;

            if (_trackQuery.HasLoop && _loop.TryGetCurrentLoop(out var closedLoop) && closedLoop.TotalLength > 0f)
            {
                var proj = _trackQuery.Project(state.Position, state.LastAnchorIndex);
                lateralOffset = proj.SignedLateralOffset;
                halfWidth = proj.HalfWidth;
                isOffTrack = proj.IsOffTrack;

                float currArc = proj.ArcLengthAlong;
                float total = closedLoop.TotalLength;
                deltaArc = currArc - ep.PrevArc;

                // Wrap-correct deltaArc. Forward seam crossing requires speed +
                // forward-aligned heading; otherwise the delta is a projection
                // jitter and gets zeroed.
                float carSpeed = state.VelocityXZ.magnitude;
                float tangentHeading = Mathf.Atan2(proj.Tangent.x, proj.Tangent.z);
                float headingErr = NormalizeAngle(tangentHeading - state.Heading);
                bool genuineForwardCrossing = carSpeed > 0.4f && Mathf.Cos(headingErr) > 0.5f;
                if (deltaArc < -total * 0.5f)
                {
                    if (genuineForwardCrossing) deltaArc += total;
                    else deltaArc = 0f;
                }
                else if (deltaArc > total * 0.5f)
                {
                    deltaArc -= total;
                }

                // Cap delta-arc by physical XZ distance moved this tick,
                // with a 1.25× tolerance for arc-vs-chord geometry. A
                // stationary agent has physicalDist ≈ 0 so deltaArc clamps to
                // 0 — prevents projection rebinds onto nearby track segments
                // from minting "free" forward progress while the car isn't
                // actually driving.
                Vector3 worldStep = state.Position - ep.PrevWorldPos;
                float physicalDist = new Vector2(worldStep.x, worldStep.z).magnitude;
                float maxDelta = physicalDist * 1.25f;
                deltaArc = Mathf.Clamp(deltaArc, -maxDelta, maxDelta);

                ep.CumulativeForwardArc += deltaArc;
                ep.PrevWorldPos = state.Position;

                // Sector-checkpoint lap counting. A lap is credited only after
                // the agent has passed through every micro-sector in order.
                // Skipping a sector or jumping backward by more than one
                // sector invalidates the lap. Robust against projection-wrap
                // onto nearby track segments.
                int microCount = Mathf.Max(closedLoop.Sectors.MicroCount, 1);
                int prevSeen = ep.LastSeenMicro;
                int curMicro = closedLoop.Sectors.MicroSectorOf(currArc);
                ep.CurrentMacro = closedLoop.Sectors.MacroSectorOf(curMicro);
                bool crossedStartLine = curMicro == 0 && prevSeen != 0;

                if (crossedStartLine)
                {
                    int fullMask = (1 << microCount) - 1;
                    bool allHit = ep.MicroSectorsHitMask == fullMask;
                    if (allHit && ep.LapValidThisGo)
                    {
                        ep.LapsCompleted++;
                        lapJustCompleted = true;
                        lapElapsedSec = now - ep.LapStartTime;
                        ep.CumulativeForwardArc = 0f;
                        // Surface lap-credit live so race telemetry can record
                        // laps without waiting for EpisodeEndedEvent (good
                        // drivers lap indefinitely and never end inside one
                        // race window).
                        Publish(new CarLapCompletedEvent(carId, ep.LapsCompleted, lapElapsedSec));
                    }
                    // Fresh attempt either way — entering S0 always restarts
                    // the gate AND the lap timer. The lap timer reset on every
                    // start-line crossing (whether the previous lap completed
                    // or was abandoned) keeps each sector's reported t-since-
                    // lap-start meaningful and prevents abandoned partial
                    // attempts from inflating later split times.
                    ep.LapStartTime = now;
                    ep.MicroSectorsHitMask = 1 << 0;
                    ep.NextRequiredMicro = 1 % microCount;
                    ep.LapValidThisGo = true;
                }
                else if (curMicro == ep.NextRequiredMicro)
                {
                    // Dual gate: geometric (curMicro matches) AND physical
                    // (capped CumulativeForwardArc has actually traversed
                    // past the boundary's arc-position-in-lap). The physical
                    // gate forces each sector credit to cost real distance —
                    // without it, projection-wrap onto self-near segments
                    // would let the agent march sector credits forward
                    // without physically driving them.
                    float requiredArcInLap = (float)ep.NextRequiredMicro * closedLoop.TotalLength / microCount;
                    const float kBoundaryEps = 0.5f;
                    bool physicalReached = ep.CumulativeForwardArc + kBoundaryEps >= requiredArcInLap;
                    if (physicalReached)
                    {
                        ep.MicroSectorsHitMask |= (1 << curMicro);
                        ep.NextRequiredMicro = (ep.NextRequiredMicro + 1) % microCount;
                        sectorJustHit = true;
                        Publish(new MicroSectorPassedEvent(carId, curMicro, ep.LapsCompleted));
                        // Telemetry: one JSONL line per sector crossed. The server
                        // groups by (car, circuit, lap) to produce split times for
                        // the fastest-laps panel. Lap number is 1-indexed (the lap
                        // currently being driven) so it matches the `laps` field on
                        // the episode-end event.
                        TrainingTelemetry.EmitMicroSector(
                            carIdHash: carId.GetHashCode(),
                            circuit: TrainingTelemetryContext.LastCircuitId,
                            lap: ep.LapsCompleted + 1,
                            sector: curMicro,
                            tLap: now - ep.LapStartTime);
                    }
                    // Geometric match but physical hasn't caught up yet:
                    // do nothing — the agent must keep driving. This is NOT
                    // an invalidation (legitimate sub-tick boundary noise
                    // should not kill the lap); the boundary will fire on a
                    // later tick once the capped cumulative distance reaches it.
                }
                else if (curMicro != prevSeen
                         && curMicro != ((ep.NextRequiredMicro - 1 + microCount) % microCount))
                {
                    // Non-adjacent jump or backward by >1 → invalidate this lap.
                    ep.LapValidThisGo = false;
                }

                ep.LastSeenMicro = curMicro;
                ep.PrevArc = currArc;
            }

            // Sample near-field curvature at 0.3 s ahead. A single near anchor
            // means MaxCurveAhead only fires when actually entering a curve;
            // sampling further ahead would saturate to 1.0 on small loops and
            // drown the progress reward.
            float maxCurveAhead = 0f;
            if (_trackQuery.HasLoop && _loop.TryGetCurrentLoop(out var loopForCurv) && loopForCurv.TotalLength > 0f)
            {
                Span<float> nearOffsets = stackalloc float[1];
                float maxSpeed = AiDriverPhysicsDefaults.Latest.MaxSpeed;
                nearOffsets[0] = Mathf.Min(0.3f * maxSpeed, loopForCurv.TotalLength * 0.4f);
                Span<CenterlineSample> curvSamples = stackalloc CenterlineSample[1];
                _trackQuery.SampleLookaheadAt(state.LastAnchorIndex, nearOffsets, curvSamples);
                float kappaScale = (maxSpeed * maxSpeed) / 8f;
                maxCurveAhead = Mathf.Clamp01(Mathf.Abs(curvSamples[0].Curvature) * kappaScale);
            }

            float speedFrac = AiDriverPhysicsDefaults.Latest.MaxSpeed > 0f
                ? Mathf.Clamp01(state.VelocityXZ.magnitude / AiDriverPhysicsDefaults.Latest.MaxSpeed)
                : 0f;

            float steer = Mathf.Clamp(state.SteerAngle, -1f, 1f);
            // Smoothness reward is computed on the steer angle only (the
            // last-applied throttle is not directly stored here). The 2×
            // scaling on the steer-delta compensates for the missing throttle
            // term. Throttle smoothness still matters indirectly via the EMA
            // action smoother in AiDriverPolicyService, which keeps
            // |Δthrottle| small at the actuator.

            float deltaSteer = ep.HasPrevAction ? Mathf.Abs(steer - ep.PrevSteer) : 0f;
            ep.PrevSteer = steer;
            ep.HasPrevAction = true;

            var ctx = new EpisodeRewardContext(
                DeltaArc: deltaArc,
                SignedLateralOffset: lateralOffset,
                HalfWidth: halfWidth,
                DeltaSteerAbs: deltaSteer,
                LapJustCompleted: lapJustCompleted,
                MaxCurveAhead: maxCurveAhead,
                SpeedFraction: speedFrac,
                OnKerb: ep.IsOnKerb,
                LapsCompleted: ep.LapsCompleted);
            float reward = EpisodeRewardCalculator.StepReward(ctx);

            // Per-micro-sector bonus. With K micro-sectors and a
            // MicroSectorBonus award each, a clean lap pays K × MicroSectorBonus
            // distributed around the loop — dense positive signal at every
            // checkpoint without dwarfing the lap-completion bonus. Skipping a
            // sector forfeits its bonus AND the lap.
            if (sectorJustHit) reward += EpisodeRewardCalculator.MicroSectorBonus;

            // Wall hits are not directly terminal. The car bounces and stuns
            // in physics; here we apply a small per-hit penalty (cooldown-
            // throttled so a stuck-against-wall car cannot be charged every
            // tick) and bump the per-episode counter so prolonged wall
            // contact eventually terminates the episode (anti-farming).
            if (ep.WallHitPending)
            {
                ep.WallHitCount++;
                if (now - ep.LastWallHitPenaltyTime >= EpisodeRewardCalculator.WallHitPenaltyCooldownSec)
                {
                    reward -= EpisodeRewardCalculator.WallHitPenalty;
                    ep.LastWallHitPenaltyTime = now;
                }
                ep.WallHitPending = false;
                TrainingTelemetry.EmitWallHit(
                    carIdHash: carId.GetHashCode(),
                    circuit: TrainingTelemetryContext.LastCircuitId,
                    x: state.Position.x, z: state.Position.z);
            }

            // Chassis health snapshot — used for both Wreck termination and
            // the clean-lap bonus modulation below.
            float health = 1f;
            if (_carSim is not null && _carSim.TryGetHealth(carId, out var h)) health = h;

            EpisodeEndReason? end = null;
            if (lapJustCompleted)
            {
                // Clean-lap bonus modulation. Damage below
                // CleanLapDamageThreshold pays the full bonus; above it the
                // bonus scales linearly to 0 at 100% damage. Encourages
                // hitting the racing line WITHOUT scraping walls.
                float damageFrac = Mathf.Clamp01(1f - health);
                float overDamage = Mathf.Max(0f, damageFrac - EpisodeRewardCalculator.CleanLapDamageThreshold);
                float cleanFactor = Mathf.Clamp01(1f - overDamage / Mathf.Max(1e-3f, 1f - EpisodeRewardCalculator.CleanLapDamageThreshold));
                // Consecutive-lap streak multiplier. Each successive lap in
                // the same episode pays a higher LapBonus, pushing the policy
                // toward sustained multi-lap consistency rather than
                // one-and-done. Linear growth with a hard cap so PPO
                // advantage stays bounded.
                float streakMul = Mathf.Min(
                    EpisodeRewardCalculator.StreakMultiplierCap,
                    1f + EpisodeRewardCalculator.ConsecutiveLapBonusGrowth * Mathf.Max(0, ep.LapsCompleted - 1));
                reward += EpisodeRewardCalculator.LapBonus * cleanFactor * streakMul;

                // Circuit-length-normalized lap-time bonus.
                //   refLap = TotalLength / (MaxSpeed × LapTimeTargetSpeedFraction)
                //   pct    = (refLap − actualLap) / refLap   (signed speedup)
                //   bonus  = sign(pct) × pct² × LapTimeBonusWeight,
                //            clamped to ±LapTimeBonusCap
                // Quadratic in fractional speed-up so each step toward a
                // faster lap pays geometrically more. Symmetric negative
                // side keeps slow laps net-bad. Gated on cleanFactor so a
                // wreck-fast lap cannot beat a clean slow one. Scale-
                // invariant — works on short ovals and long authored loops.
                // Example: 20% faster than par ≈ +32, 30% ≈ +72, 40%+ → cap.
                float maxSpeed = AiDriverPhysicsDefaults.Latest.MaxSpeed;
                float refLapSec = (_loop.TryGetCurrentLoop(out var lapLoop) && lapLoop.TotalLength > 0f && maxSpeed > 0f)
                    ? lapLoop.TotalLength / (maxSpeed * EpisodeRewardCalculator.LapTimeTargetSpeedFraction)
                    : EpisodeRewardCalculator.LapTimeFallbackTargetSec;
                float pctBetter = (refLapSec - lapElapsedSec) / Mathf.Max(0.1f, refLapSec);
                float signedSq = Mathf.Sign(pctBetter) * pctBetter * pctBetter;
                float timeBonus = Mathf.Clamp(
                    signedSq * EpisodeRewardCalculator.LapTimeBonusWeight,
                    -EpisodeRewardCalculator.LapTimeBonusCap,
                    EpisodeRewardCalculator.LapTimeBonusCap);
                reward += timeBonus * cleanFactor;

                // Multi-lap live telemetry: emit a `lap_complete` event per
                // lap-cross so the dashboard's lap log and per-circuit stats
                // populate without waiting for the eventual episode-end.
                float lapDeltaReward = (ep.CumulativeReward + reward) - ep.LapStartReward;
                int lapDeltaWallHits = ep.WallHitCount - ep.LapStartWallHits;
                int lapDeltaSteps = Mathf.Max(0, (int)(lapElapsedSec * 50f)); // SIM_HZ=50 matches server
                TrainingTelemetry.EmitLap(
                    carIdHash: carId.GetHashCode(),
                    circuit: TrainingTelemetryContext.LastCircuitId,
                    lap: ep.LapsCompleted,
                    lapSec: lapElapsedSec,
                    lapSteps: lapDeltaSteps,
                    lapReward: lapDeltaReward,
                    wallHitsThisLap: lapDeltaWallHits,
                    // Lap 1 = cold (started at spawn pose, accelerating from rest).
                    // Lap >=2 = flying (rolled across S0 at race speed from previous lap).
                    isFlying: ep.LapsCompleted >= 2,
                    health: health);
                ep.LapStartReward = ep.CumulativeReward + reward;
                ep.LapStartWallHits = ep.WallHitCount;

                // Lap completion is not terminal. Lap bonus + time bonus
                // still pay per crossing, but the car keeps its velocity and
                // continues into the next lap. Episodes end only on wreck /
                // wall-cap / off-track / max-steps. The sector mask and lap
                // timer have already been reset above on the start-line
                // crossing — this lets the policy learn lap-to-lap
                // consistency rather than one-shot closure, and lets a
                // playback / test scene show visible continuous racing
                // without stopping at the start/finish line.

                // Race-scoped: once the driver banks LapTarget laps, the
                // race result is locked in for them — publish the finish
                // event with a position-scaled bonus, mark Resolved, and
                // let the agent idle on zero reward until RaceEndedEvent
                // arrives. Eliminated drivers stop accumulating reward via
                // ResolveDriverEliminated; finishers stop here.
                if (IsRaceScoped && ep.LapsCompleted >= _coord.LapTarget && !ep.RaceResolvedReason.HasValue)
                {
                    int position = _coord.GetFinishersCount() + 1;
                    // Finish bonus halves with each successive position so
                    // pole pays double the second place car. Capped at 4×
                    // total LapBonus so PPO advantage stays well-conditioned
                    // when many drivers finish in the same race.
                    float finishMul = 2f / Mathf.Max(1, position);
                    float finishBonus = EpisodeRewardCalculator.LapBonus * finishMul;
                    reward += finishBonus;
                    float totalRaceTime = now - ep.StartTime;
                    Publish(new DriverFinishedRaceEvent(carId, position, totalRaceTime, ep.LapsCompleted));
                    ep.RaceResolvedReason = EpisodeEndReason.Success;
                }
            }
            else if (health <= 0f)
            {
                // Chassis wrecked from accumulated wall damage — primary
                // crash failure mode. Penalty tapers post-first-lap so the
                // pre-skill anti-suicide buffer doesn't bloat the reward
                // distribution during the racing-skill regime.
                reward -= ep.LapsCompleted >= 1
                    ? EpisodeRewardCalculator.WreckPenaltyPostFirstLap
                    : EpisodeRewardCalculator.WreckPenalty;
                if (IsRaceScoped) ResolveDriverEliminated(carId, ref ep, EpisodeEndReason.Failure_Wreck);
                else end = EpisodeEndReason.Failure_Wreck;
            }
            else if (ep.WallHitCount >= EpisodeRewardCalculator.MaxWallHitsPerEpisode)
            {
                // Backstop: stuck against a wall but not yet wrecked (e.g.
                // low-speed parallel-parking-into-wall). End the episode so
                // the policy sees a clean failure signal instead of a long
                // timeout.
                reward -= EpisodeRewardCalculator.WallHitPenalty;
                if (IsRaceScoped) ResolveDriverEliminated(carId, ref ep, EpisodeEndReason.Failure_WallHit);
                else end = EpisodeEndReason.Failure_WallHit;
            }
            else if (ep.OffTrackTimer > OffTrackTimeoutSec)
            {
                reward -= EpisodeRewardCalculator.OffTrackPenalty;
                if (IsRaceScoped) ResolveDriverEliminated(carId, ref ep, EpisodeEndReason.Failure_OffTrack);
                else end = EpisodeEndReason.Failure_OffTrack;
            }
            else if (ep.Steps >= MaxStepsPerEpisode)
            {
                // Multi-lap mode: hitting the step cap splits two ways:
                //  • lapsCompleted > 0  → Success (at least one full lap
                //    was banked; bonuses already paid at each crossing).
                //  • lapsCompleted == 0 → Failure_Timeout (the milking
                //    pathology — drove forever, never closed).
                if (IsRaceScoped)
                {
                    // Race-scoped per-car step cap = elimination. The
                    // coordinator's MaxRaceSteps catches whole-race stalls
                    // separately; per-car cap just means this driver burned
                    // its budget without reaching lap_target.
                    var stepCapReason = ep.LapsCompleted > 0
                        ? EpisodeEndReason.Success
                        : EpisodeEndReason.Failure_Timeout;
                    if (stepCapReason == EpisodeEndReason.Failure_Timeout)
                        reward -= EpisodeRewardCalculator.TimeoutPenalty;
                    ResolveDriverEliminated(carId, ref ep, stepCapReason);
                }
                else
                {
                    end = ep.LapsCompleted > 0
                        ? EpisodeEndReason.Success
                        : EpisodeEndReason.Failure_Timeout;
                }
            }

            // Terminal fractional-progress bonus on ANY end. Provides a
            // dense gradient toward closing — partial laps still get partial
            // reward proportional to forward arc covered, so an episode that
            // ends 80% around the loop pays more than one that ends 30%.
            if (end.HasValue && end.Value != EpisodeEndReason.Aborted
                && _trackQuery.HasLoop && _loop.TryGetCurrentLoop(out var endLoop)
                && endLoop.TotalLength > 0f)
            {
                float totalDistance = ep.LapsCompleted * endLoop.TotalLength + ep.CumulativeForwardArc;
                float fraction = Mathf.Clamp(totalDistance / endLoop.TotalLength, 0f, 2f);
                reward += EpisodeRewardCalculator.TerminalProgressFractionWeight * fraction;
            }

            // Timeout penalty: the explicit terminal cost makes Timeout
            // strictly worse than Wreck and OffTrack so the cleanest exit
            // from any episode is finishing a lap (or at least dying trying).
            // Without this the policy can settle into "milk per-step
            // bonuses, never close the loop".
            if (end.HasValue && end.Value == EpisodeEndReason.Failure_Timeout)
            {
                reward -= EpisodeRewardCalculator.TimeoutPenalty;
            }

            ep.CumulativeReward += reward;

            // Periodic trajectory sample for the dashboard heatmap. Skipped
            // on terminal ticks (PublishEnd already emits an episode_end with
            // position) and only fires every StepSampleInterval to keep the
            // jsonl files compact.
            if (sampleThisStep && !end.HasValue)
            {
                float lapFracNow = 0f;
                if (_loop.TryGetCurrentLoop(out var loopForSample) && loopForSample.TotalLength > 0f)
                    lapFracNow = Mathf.Clamp(ep.CumulativeForwardArc / loopForSample.TotalLength, 0f, 1f);
                TrainingTelemetry.EmitStepSample(
                    carIdHash: carId.GetHashCode(),
                    circuit: TrainingTelemetryContext.LastCircuitId,
                    x: state.Position.x, z: state.Position.z,
                    lapFraction: lapFracNow,
                    speed: state.VelocityXZ.magnitude);
            }

            if (end.HasValue)
            {
                PublishEnd(carId, ep, end.Value);
                return new StepResult(reward, end);
            }

            _episodes[carId] = ep;
            return new StepResult(reward, null);
        }

        public void OnAgentUnregistered(CarId carId)
        {
            _episodes.Remove(carId);
        }

        private void OnOffTrack(CarOffTrackEvent evt)
        {
            if (_episodes.TryGetValue(evt.Id, out var ep))
            {
                ep.IsOffTrack = true;
                _episodes[evt.Id] = ep;
            }
        }

        private void OnBackOnTrack(CarBackOnTrackEvent evt)
        {
            if (_episodes.TryGetValue(evt.Id, out var ep))
            {
                ep.IsOffTrack = false;
                ep.OffTrackTimer = 0f;
                _episodes[evt.Id] = ep;
            }
        }

        private void OnHitWall(CarHitWallEvent evt)
        {
            if (_episodes.TryGetValue(evt.Id, out var ep))
            {
                ep.WallHitPending = true;
                _episodes[evt.Id] = ep;
            }
        }

        // Race-scoped helper: stamp the driver as Eliminated, fire the
        // coordinator-bound event, and physically despawn the car so wrecks
        // don't linger as static obstacles for the rest of the race.
        // PPO episode is NOT ended here — coord.ShouldEndCarEpisode will
        // flip true at the next race boundary.
        private void ResolveDriverEliminated(CarId carId, ref EpisodeState ep, EpisodeEndReason reason)
        {
            if (ep.RaceResolvedReason.HasValue) return;
            ep.RaceResolvedReason = reason;
            Publish(new DriverEliminatedEvent(carId, reason));
            _carSim.Despawn(carId);
        }

        private void PublishEnd(CarId carId, in EpisodeState ep, EpisodeEndReason reason)
        {
            float elapsed = _time.Time - ep.StartTime;

            // Snapshot circuit + end pose + lap fraction BEFORE publishing the
            // EpisodeEndedEvent. Subscribers (TrainingDirector) regenerate the
            // loop synchronously, which mutates TrainingTelemetryContext.LastCircuitId
            // and emits its own circuit_change line. If we read the context AFTER
            // Publish, every Success episode_end gets tagged with the NEXT
            // circuit's id and the dashboard correlates lap splits to the wrong
            // circuit. Snapshot pattern guarantees telemetry sees the circuit
            // the lap actually ran on.
            string circuitAtEnd = TrainingTelemetryContext.LastCircuitId;
            float endX = 0f, endZ = 0f;
            if (_carSim.TryGetState(carId, out var endState))
            {
                endX = endState.Position.x;
                endZ = endState.Position.z;
            }
            float endHealth = 1f;
            _carSim.TryGetHealth(carId, out endHealth);
            float lapFraction = 0f;
            if (_loop.TryGetCurrentLoop(out var endLoop) && endLoop.TotalLength > 0f)
                lapFraction = Mathf.Clamp(ep.CumulativeForwardArc / endLoop.TotalLength, 0f, 1f);

            Publish(new EpisodeEndedEvent(
                Car: carId,
                Reason: reason,
                CumulativeReward: ep.CumulativeReward,
                Steps: ep.Steps,
                ElapsedSec: elapsed,
                LapsCompleted: ep.LapsCompleted));

            // Emit per-episode telemetry for the dashboard.
            TrainingTelemetry.EmitEpisodeEnd(
                carIdHash: carId.GetHashCode(),
                circuit: circuitAtEnd,
                endReason: reason.ToString(),
                endX: endX,
                endZ: endZ,
                lapFraction: lapFraction,
                lapsCompleted: ep.LapsCompleted,
                steps: ep.Steps,
                elapsedSec: elapsed,
                cumulativeReward: ep.CumulativeReward,
                wallHitCount: ep.WallHitCount,
                health: endHealth);

            _episodes.Remove(carId);
            if (reason == EpisodeEndReason.Aborted) _loopOpenAbortPending = false;

            _endReasonCounts.TryGetValue(reason, out int prev);
            _endReasonCounts[reason] = prev + 1;
            _totalEnded++;
            if (reason == EpisodeEndReason.Success) _successesInWindow++;

            if (_totalEnded % LogEveryNEpisodes == 0)
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("[EpisodeRunner] last ").Append(LogEveryNEpisodes)
                  .Append(" of ").Append(_totalEnded)
                  .Append(": completion=").Append(_successesInWindow)
                  .Append('/').Append(LogEveryNEpisodes)
                  .Append(' ');
                foreach (var kv in _endReasonCounts) sb.Append(kv.Key).Append('=').Append(kv.Value).Append(' ');
                Debug.Log(sb.ToString());
                _successesInWindow = 0;
            }
        }

        private static float NormalizeAngle(float a)
        {
            while (a > Mathf.PI) a -= 2f * Mathf.PI;
            while (a < -Mathf.PI) a += 2f * Mathf.PI;
            return a;
        }

        private struct EpisodeState
        {
            public float StartTime;
            public float LastTickTime;
            public float LapStartTime;
            public float PrevArc;
            public Vector3 PrevWorldPos;
            public float CumulativeForwardArc;
            public bool IsOffTrack;
            public float OffTrackTimer;
            public int Steps;
            public int LapsCompleted;
            public float CumulativeReward;
            public float PrevSteer;
            public float PrevThrottle;
            public bool HasPrevAction;
            // Set by CarHitWallEvent handler; checked in PostStep so the
            // termination happens during the next regular tick (keeps reward
            // accounting + ML-Agents EndEpisode call on the agent's thread).
            public bool WallHitPending;
            // Cooldown timestamp + per-episode counter to stop reward
            // farming via wall taps (the wall-hit penalty is applied at
            // most once per WallHitPenaltyCooldownSec, and after
            // MaxWallHitsPerEpisode hits the episode ends as
            // Failure_WallHit so the policy can't sit stuck against a
            // wall accumulating progress).
            public float LastWallHitPenaltyTime;
            public int WallHitCount;
            // Tracks the most recent CarOnKerbEvent / CarOffKerbEvent so
            // PostStep can apply a small per-tick "you found the racing
            // line" bonus without re-querying the collision service.
            public bool IsOnKerb;
            // Sector-checkpoint lap-counting state. NextRequiredMicro is the
            // sector the agent must enter next to advance; LastSeenMicro is
            // the sector containing the previous tick's arc projection.
            // MicroSectorsHitMask is a K-bit bitmask of sectors visited in
            // the current lap attempt; LapValidThisGo flips false on any
            // non-adjacent jump and gates the lap counter on next start-line
            // crossing.
            public int NextRequiredMicro;
            public int LastSeenMicro;
            public int MicroSectorsHitMask;
            public int CurrentMacro;
            public bool LapValidThisGo;
            // Multi-lap telemetry: snapshots of reward + wall hits taken at
            // the previous lap-cross so EmitLap can report per-lap delta
            // values (lapReward, wallHitsThisLap).
            public float LapStartReward;
            public int LapStartWallHits;
            // Race-scoped mode only: latched the first time the driver
            // finished the lap target or was eliminated. Stops per-step
            // reward accrual + suppresses repeat publishes while the agent
            // waits for the race-end signal. The reason is then carried
            // into the eventual EpisodeEndedEvent.
            public EpisodeEndReason? RaceResolvedReason;
        }
    }

    /// <summary>
    /// Fires once per micro-sector boundary crossed in the correct lap order.
    /// Subscribers: training telemetry, future spectator HUD.
    /// </summary>
    public readonly record struct MicroSectorPassedEvent(CarId CarId, int Micro, int LapNumber);

    /// <summary>Inputs to <see cref="EpisodeRewardCalculator.StepReward"/>.</summary>
    public readonly record struct EpisodeRewardContext(
        float DeltaArc,
        float SignedLateralOffset,
        float HalfWidth,
        float DeltaSteerAbs,
        bool LapJustCompleted,
        float MaxCurveAhead = 0f,
        float SpeedFraction = 0f,
        bool OnKerb = false,
        int LapsCompleted = 0);

    /// <summary>
    /// Potential-based reward calculator. Per-step shaping plus terminal
    /// bonuses / penalties resolved by <see cref="EpisodeRunner"/>.
    /// <code>
    /// step = ProgressWeight·Δarc
    ///      − LateralWeight·(lat/halfWidth)²
    ///      − SmoothnessWeight·|Δsteer|
    ///      − CurveTooFastWeight·MaxCurveAhead·speedFrac²
    ///      + OnKerbBonus
    ///      + SpeedRewardWeight·speedFrac
    ///      + SpeedSquaredWeight·speedFrac²
    ///      + AliveBonus
    ///      − TimeCostPerStep
    /// </code>
    /// </summary>
    public static class EpisodeRewardCalculator
    {
        /// <summary>
        /// Per-step weight on forward arc-length progress. Dominant positive
        /// signal once the policy is moving.
        /// UP: forward progress dominates everything else; the policy will
        /// drive even if it has to scrape walls to do it.
        /// DOWN: progress competes with other shaping; risks the policy
        /// stalling for "alive" reward.
        /// </summary>
        public const float ProgressWeight = 2.0f;

        /// <summary>
        /// Per-step penalty on squared lateral offset (clamped). Encourages
        /// staying near the centerline.
        /// UP: cars hug the centerline aggressively — kills apex and racing
        /// line behaviour.
        /// DOWN: lateral position is free; the policy can wander toward kerbs
        /// and shortcuts.
        /// </summary>
        public const float LateralWeight = 0.02f;

        /// <summary>
        /// Per-step alive reward applied BEFORE the car has completed its
        /// first lap. Sized above <see cref="TimeCostPerStep"/> so being on
        /// track at all is net reward-positive — kills the suicide-via-wall
        /// gradient that emerges under the canonical physics when a poor-skill policy
        /// cannot outrun TimeCost and WreckPenalty becomes the cheapest
        /// exit. After the first lap closes the policy has demonstrated
        /// basic competence and the bonus tapers to
        /// <see cref="AliveBonusPostFirstLap"/> so milking ("drive forever,
        /// never close") stops being viable.
        /// UP: stronger anti-suicide gradient pre-skill, but heavier
        /// milking risk if LapBonus regresses.
        /// DOWN: weaker safety net while learning to drive.
        /// </summary>
        public const float AliveBonus = 0.05f;

        /// <summary>
        /// Per-step alive reward after at least one lap has been completed.
        /// Sized BELOW <see cref="TimeCostPerStep"/> (0.04) so post-skill
        /// the policy is net-negative on idle ticks and must pay its way
        /// with progress/sector/lap bonuses. The taper is a step at lap 1
        /// (not linear) because lap-1 closure is the binary signal that the
        /// policy has escaped the suicide basin.
        /// UP: more breathing room post-skill, but milking risk grows.
        /// DOWN (0): tight post-skill — every tick must earn its reward.
        /// </summary>
        public const float AliveBonusPostFirstLap = 0.01f;

        /// <summary>
        /// Per-step penalty on |Δsteer|. Encourages smooth wheel inputs.
        /// UP: cars steer like luxury sedans — strong penalty on twitch.
        /// DOWN: micro-corrections are free; throttle/steer wobble allowed.
        /// </summary>
        public const float SmoothnessWeight = 0.03f;

        /// <summary>
        /// Terminal bonus for completing a lap (paid at every lap-cross, not
        /// only at episode end).
        /// UP: lap closure dominates the reward landscape; cars learn to
        /// finish even on chaotic circuits.
        /// DOWN: closure pays little vs per-step progress; partial laps
        /// become attractive.
        /// </summary>
        public const float LapBonus = 500.0f;

        /// <summary>
        /// Linear growth coefficient on the per-lap streak multiplier:
        /// <c>multiplier = min(StreakMultiplierCap, 1 + Growth × (N − 1))</c>
        /// where N is the 1-indexed lap number.
        /// UP: extra laps in the same episode pay disproportionately more;
        /// pushes multi-lap consistency.
        /// DOWN: streaks decay flat; one-and-done laps look as good as long
        /// stints.
        /// </summary>
        public const float ConsecutiveLapBonusGrowth = 0.5f;

        /// <summary>
        /// Hard cap on the streak multiplier. Keeps PPO advantage bounded so
        /// the critic does not blow up trying to baseline very long stints.
        /// UP: long sessions pay huge end-of-stint laps — high variance.
        /// DOWN: streak bonus quickly maxes out; little incentive to chase
        /// long stints.
        /// </summary>
        public const float StreakMultiplierCap = 4.0f;

        /// <summary>
        /// Terminal penalty for ending off-track. Sized to make shortcuts
        /// across grass strictly net-negative.
        /// UP: shortcutting is unthinkable; cars stay rigidly on tarmac.
        /// DOWN: the policy will trade off-track time for raw progress.
        /// </summary>
        public const float OffTrackPenalty = 10.0f;

        /// <summary>
        /// Per-hit penalty on wall contact (cooldown-throttled by
        /// <see cref="WallHitPenaltyCooldownSec"/>).
        /// UP: every wall touch is expensive — drives cleaner lines.
        /// DOWN: contact is cheap; wall-riding becomes a viable line.
        /// </summary>
        public const float WallHitPenalty = 2.0f;

        /// <summary>
        /// Minimum interval between wall-hit penalty applications, seconds.
        /// UP: stuck-against-wall scenarios accrue penalty slowly; gentler
        /// gradient.
        /// DOWN: every tick of wall contact is charged; harsher per-second
        /// cost for getting stuck.
        /// </summary>
        public const float WallHitPenaltyCooldownSec = 0.5f;

        /// <summary>
        /// Per-episode hard cap on wall hits before terminating with
        /// <see cref="EpisodeEndReason.Failure_WallHit"/>. Set to 40 — looser
        /// than a tight 25 because the canonical physics has higher
        /// wall-contact rates from softer rebound. The earlier int.MaxValue
        /// experiment showed ~25% of drivers grinding walls indefinitely
        /// without ever reaching health=0 (no Failure_Wreck → race-flush
        /// sweep at end); the cap restores a clean failure signal.
        /// Each wall tap still costs the normal <see cref="WallHitPenalty"/>
        /// + impact damage in reward space; the cap is a hard backstop so
        /// pinned cars receive a clean failure signal instead of a 200s
        /// zero-reward tail that poisons the value function.
        /// </summary>
        public const int MaxWallHitsPerEpisode = 40;

        /// <summary>
        /// Terminal multiplier on cumulative forward-arc fraction. Provides
        /// a dense gradient toward lap closure: half-lap = 0.5 × this.
        /// UP: even partial-lap episodes earn meaningful reward — softer
        /// failure signal.
        /// DOWN: the policy gets reward only for closing the lap.
        /// </summary>
        public const float TerminalProgressFractionWeight = 0.0f;

        /// <summary>
        /// Per-tick bonus when the car is on a kerb. Currently disabled — the
        /// kerb incentive lives in physics (higher grip + bypassing the
        /// off-kerb cornering penalty) so the policy is not pushed onto kerbs
        /// purely for free reward.
        /// UP: cars actively seek kerbs even where it costs lap time.
        /// DOWN (0): kerb usage is decided purely by physics gain.
        /// </summary>
        public const float OnKerbBonus = 0.0f;

        /// <summary>
        /// Penalty weight on <c>MaxCurveAhead × speedFrac²</c>. Was an
        /// artificial shaping term that charged the car for entering an
        /// upcoming corner too fast. Disabled (0) because the canonical
        /// physics already make "too fast for the apex" expensive on its own —
        /// halved brake force, +1/3 sliding, and the lateral-grip cut at
        /// speed mean a corner taken too hot punishes itself with a slide
        /// → wall contact → wreck penalty. Letting physics do it produces
        /// a cleaner gradient than a shaping term tuned to a specific
        /// brake force.
        /// UP: artificial corner-entry penalty returns; can dominate at
        /// high CurveAhead values.
        /// DOWN (0): physics is the only corner-speed teacher. Recommended.
        /// </summary>
        public const float CurveTooFastWeight = 0.0f;

        /// <summary>
        /// Per-step weight linear in <c>speedFraction</c>. Direct gradient
        /// toward higher cruise speed.
        /// UP: cars push for raw speed even at the cost of cornering grip.
        /// DOWN: speed is neutral; only arc progress matters.
        /// </summary>
        public const float SpeedRewardWeight = 0.02f;

        /// <summary>
        /// Flat per-step cost. Soft time pressure: net per-step reward is
        /// negative when nothing is happening, so stalling for time is never
        /// a winning strategy.
        /// UP: episodes feel rushed; the policy commits to closure even at
        /// risk.
        /// DOWN: the policy can dawdle and still net positive reward.
        /// </summary>
        public const float TimeCostPerStep = 0.04f;

        /// <summary>
        /// Per-step weight on <c>speedFraction²</c>. Disproportionately
        /// rewards reaching the top of the speed envelope (where the aero
        /// boost lives).
        /// UP: the policy chases laser-straight top-end speed.
        /// DOWN (0): speed reward is linear; no extra pull toward the
        /// envelope edge.
        /// </summary>
        public const float SpeedSquaredWeight = 0.0f;

        /// <summary>
        /// Per-micro-sector bonus, awarded each time the agent enters the
        /// next required micro-sector in order. Distributes a dense positive
        /// signal around the loop without dwarfing the lap bonus.
        /// UP: sector checkpoints dominate the per-step signal; cars race
        /// from sector to sector.
        /// DOWN: per-sector reward is a small nudge; closure is the real prize.
        /// </summary>
        public const float MicroSectorBonus = 1.5f;

        /// <summary>
        /// Damage fraction below which a finished lap pays the full
        /// <see cref="LapBonus"/>. Above this, the bonus scales linearly to 0
        /// at 100% damage.
        /// UP: more permissive — paint-trading laps still pay nearly full bonus.
        /// DOWN: any chassis scrape costs lap reward; only pristine laps pay full.
        /// </summary>
        public const float CleanLapDamageThreshold = 0.02f;

        /// <summary>
        /// Terminal penalty when chassis health reaches 0, applied BEFORE
        /// the car has completed its first lap. Sized large (60) so during
        /// the learning phase wrecking is the worst possible outcome and
        /// the policy is forced to pursue *any* alternative — combined with
        /// <see cref="AliveBonus"/> (0.05 > TimeCostPerStep 0.04) this means
        /// even an idle car has higher expected return than crashing, which
        /// kills the suicide-via-wall basin under the canonical physics. After lap 1
        /// the penalty tapers to <see cref="WreckPenaltyPostFirstLap"/> so
        /// the reward distribution narrows and PPO advantage estimation
        /// stays well-conditioned for the racing-skill regime.
        /// UP: wrecks are catastrophic; the policy avoids any risk pre-skill.
        /// DOWN: wrecks are cheap; the policy is willing to crash for progress.
        /// </summary>
        public const float WreckPenalty = 100.0f;

        /// <summary>
        /// Terminal penalty when chassis health reaches 0 after at least
        /// one lap has been completed. Lower than <see cref="WreckPenalty"/>
        /// because the policy has demonstrated it can drive — wreck no
        /// longer needs the maximum-deterrent treatment. Post-skill suicide
        /// is gated by the PPO value function (V(state) reflects expected
        /// future LapBonus, so wrecking carries negative advantage even
        /// when raw idle-vs-wreck math favors wrecking).
        /// UP: post-skill wrecks still hurt; conservative racing emerges.
        /// DOWN: post-skill wrecks barely register; chaos mid-pack returns.
        /// </summary>
        public const float WreckPenaltyPostFirstLap = 30.0f;

        /// <summary>
        /// Terminal penalty applied on <see cref="EpisodeEndReason.Failure_Timeout"/>.
        /// Sized larger than <see cref="WreckPenalty"/> and
        /// <see cref="OffTrackPenalty"/> so timing out is strictly the worst
        /// exit.
        /// UP: timeouts are crippling — the policy must commit to closure.
        /// DOWN: timing out is acceptable; milking strategies become viable.
        /// </summary>
        public const float TimeoutPenalty = 30.0f;

        /// <summary>
        /// Target average speed fraction over a "par" lap, used to compute
        /// the reference lap time:
        /// <c>refLap = TotalLength / (MaxSpeed × this)</c>.
        /// UP: par lap shrinks (harder target); only the very fastest laps
        /// beat it.
        /// DOWN: par lap is generous; even slow clean laps earn positive
        /// time-bonus.
        /// </summary>
        public const float LapTimeTargetSpeedFraction = 0.55f;

        /// <summary>
        /// Multiplier on the signed-squared lap-time speedup:
        /// <c>bonus = sign(pct) × pct² × this</c>, clamped to
        /// ±<see cref="LapTimeBonusCap"/>. Quadratic shape compounds the
        /// gradient toward faster laps.
        /// UP: lap-time bonus dominates terminal reward; the policy
        /// optimises for raw pace.
        /// DOWN: closing the lap matters more than how fast.
        /// </summary>
        public const float LapTimeBonusWeight = 800f;

        /// <summary>
        /// Absolute cap on the symmetric lap-time bonus / penalty. Bounds
        /// PPO advantage so the critic stays stable.
        /// UP: extreme lap times pay extreme rewards — higher variance.
        /// DOWN: cap clips quickly; differential between fast and very-fast
        /// laps flattens.
        /// </summary>
        public const float LapTimeBonusCap = 120f;

        /// <summary>
        /// Fallback reference lap time used only when <see cref="IClosedLoopService"/>
        /// briefly has no current loop. Vanishingly rare; the constant keeps
        /// the code path safe.
        /// </summary>
        public const float LapTimeFallbackTargetSec = 40f;

        public static float StepReward(in EpisodeRewardContext ctx)
        {
            float progress = ProgressWeight * ctx.DeltaArc;

            // Clamp ratio to ±2 so lateral penalty caps at -0.08/tick.
            // Without the clamp, agents far off-track (5x halfwidth) hit
            // -0.50/tick × 25 ticks = -12.5 — drowning all other signal
            // and giving uniform negative returns (advantage collapse).
            float lateral = 0f;
            if (ctx.HalfWidth > 1e-6f)
            {
                float ratio = Mathf.Clamp(ctx.SignedLateralOffset / ctx.HalfWidth, -2f, 2f);
                lateral = -LateralWeight * ratio * ratio;
            }

            float smoothness = -SmoothnessWeight * ctx.DeltaSteerAbs;

            // Going-too-fast-into-curve. Quadratic in speed → high speed
            // into curve gets harsh penalty, low speed essentially free.
            float curveTooFast = -CurveTooFastWeight * ctx.MaxCurveAhead
                                 * ctx.SpeedFraction * ctx.SpeedFraction;

            float kerb = ctx.OnKerb ? OnKerbBonus : 0f;
            // Speed shaping: linear in SpeedFraction (0..1).
            float speed = SpeedRewardWeight * Mathf.Clamp01(ctx.SpeedFraction);
            // Quadratic kicker — disproportionately rewards top-of-envelope
            // speed so the aero-boost mechanic has a clean gradient: hold
            // straight → cap rises → squared bonus pays more than linear.
            float sf = Mathf.Clamp01(ctx.SpeedFraction);
            float speedSq = SpeedSquaredWeight * sf * sf;
            // Taper alive bonus: full while learning (laps == 0), small post-
            // skill (laps >= 1). Avoids both the suicide trap and the milking
            // strategy in one piecewise constant.
            float aliveBonus = ctx.LapsCompleted >= 1 ? AliveBonusPostFirstLap : AliveBonus;
            return progress + lateral + smoothness + curveTooFast + kerb + aliveBonus
                   + speed + speedSq - TimeCostPerStep;
        }
    }
}
