using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.AiDriver.Physics;
using UnityPpoRacingTrainer.Core.AiDriver.Physics.Draft;
using UnityPpoRacingTrainer.Core.AiDriver.Physics.Fuel;
using UnityPpoRacingTrainer.Core.AiDriver.Physics.Tires;
using UnityPpoRacingTrainer.Core.AiDriver.Policy;
using UnityPpoRacingTrainer.Core.AiDriver.Race;
using UnityPpoRacingTrainer.Core.AiDriver.Training;
using Unidad.Core.EventBus;
using Unidad.Core.Systems;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.AiDriver.Telemetry
{
    /// <summary>
    /// Core race-telemetry pump. Subscribes to the existing physics + race
    /// event stream, builds an in-flight <see cref="RaceRecordDto"/> per
    /// race, samples per-driver state at <see cref="SampleHz"/> (5 Hz), and
    /// flushes a complete race to the sink once <c>expectedDriversPerRound</c>
    /// distinct EpisodeEndedEvents have fired since the last flush. An
    /// optional <see cref="WriteIntervalSeconds"/> throttle drops races that
    /// finish before the interval expires; default 0 = write every complete
    /// round.
    ///
    /// The completion gate counts events, not dict entries: the
    /// <c>_drivers</c> map is keyed by CarId and ML-Agents respawns mint a
    /// fresh CarId every episode, so the dict grows unbounded with stale
    /// entries. The old AllDriversEnded() gate (every dict entry Ended=true)
    /// was therefore unsatisfiable in multi-round training and was removed.
    ///
    /// The recorder is intentionally a passive listener: it never injects
    /// into the physics step, so the simulation is bit-identical whether or
    /// not telemetry is registered.
    ///
    /// The reservoir sampler is kept in the constructor signature for
    /// dependency-injection compatibility but is no longer consulted.
    /// </summary>
    internal sealed class RaceTelemetryService : SystemServiceBase, IRaceTelemetryRecorder
    {
        public const double DefaultWriteIntervalSeconds = 0.0;
        public const int DefaultExpectedDriversPerRound = 24;
        public const int SampleHz = 5;
        private const float SamplePeriod = 1f / SampleHz;

        // Caps to prevent unbounded growth when the counter gate
        // mis-fires (one car never publishes EpisodeEndedEvent → dict and
        // sample lists grow forever). User wants HIGH per-driver sample
        // density (offset by fewer kept races on disk — see DiskJsonRaceSink
        // kept count in TrainerBootstrap), so the per-driver cap is wide;
        // the watchdog is what actually catches a stuck race.
        // 5 Hz × 1800 s = 9000 samples ≈ 30 min of telemetry per driver.
        private const int MaxSamplesPerDriver = 9000;
        private const int SampleTrimSlack = 256;
        // Event log is sparse (lap_complete + collisions) but a stuck race
        // with 24 cars driving in circles can hit thousands of wall_hits.
        private const int MaxEventsPerRace = 4000;
        // Wall-clock watchdog. A real PPO episode wraps in well under 60 s;
        // anything still open after 60 s is leaking → force-flush. Tighter
        // than the prior 120 s ceiling because the trainer was bleeding
        // ~100 MB/min and any reduction in in-flight peak helps.
        private const double WatchdogMaxRaceSeconds = 60.0;
        // Counter-gate is _endedThisRound; this is a hard ceiling on the
        // dict size that bypasses the gate when ML-Agents respawns keep
        // minting fresh CarIds without enough corresponding episode_end.
        // 4× expected = forgiving but bounded.
        private const int WatchdogDriverMultiple = 4;
        // Throttle the watchdog check inside OnTick (50 Hz × N cars is hot).
        private const int WatchdogTickStride = 256;

        private readonly IRaceTelemetrySink _sink;
        private readonly ITirePhysicsService _tires;
        private readonly IFuelService _fuel;
        private readonly IDraftService _draft;
        private readonly IRaceStateService _raceState;
        // Optional. When non-null and IsRaceScoped, race boundaries are
        // strict: BeginRace on RaceStartedEvent, FinalizeAndFlush on
        // RaceEndedEvent. The counter-gate, watchdog, and CircuitRegen
        // hard-flush paths are skipped because the coordinator owns the
        // race lifecycle and guarantees those flush points.
        private readonly IRaceCoordinator _coord;
        private readonly Func<DateTime> _clock;
        private readonly int _expectedDriversPerRound;

        private RaceRecordDto _inFlight;
        private readonly Dictionary<CarId, DriverInFlight> _drivers = new();
        private DateTime _raceStartUtc;
        private DateTime _nextEligibleWriteUtc = DateTime.MinValue;
        private float _maxLocalTime;
        private long _processEpisodeCounter;
        private int _endedThisRound;
        private int _watchdogTickCounter;

        /// <summary>
        /// Minimum wall-clock seconds between race writes. Default 0 = write
        /// every completed round. Bump to throttle on disk-fill (the
        /// supervisor's typical setting).
        /// </summary>
        public double WriteIntervalSeconds { get; set; } = DefaultWriteIntervalSeconds;

        public RaceTelemetryService(
            IEventBus eventBus,
            IRaceTelemetrySink sink,
            ReservoirRaceSampler sampler,
            ITirePhysicsService tires = null,
            IFuelService fuel = null,
            IDraftService draft = null,
            IRaceStateService raceState = null,
            Func<DateTime> clock = null,
            int expectedDriversPerRound = DefaultExpectedDriversPerRound,
            IRaceCoordinator coord = null) : base(eventBus)
        {
            _sink = sink;
            // sampler ignored — kept for DI parameter parity. Counter gate
            // replaces reservoir sampling.
            _ = sampler;
            _tires = tires;
            _fuel = fuel;
            _draft = draft;
            _raceState = raceState;
            _coord = coord;
            _clock = clock ?? (() => DateTime.UtcNow);
            _expectedDriversPerRound = expectedDriversPerRound > 0
                ? expectedDriversPerRound
                : DefaultExpectedDriversPerRound;

            Subscribe<CarSpawnedEvent>(OnSpawn);
            Subscribe<CarDespawnedEvent>(OnDespawn);
            Subscribe<CarPhysicsTickedEvent>(OnTick);
            Subscribe<CarLapCompletedEvent>(OnLap);
            Subscribe<CarHitWallEvent>(OnWallHit);
            Subscribe<CarHitCarEvent>(OnCarHit);
            Subscribe<TirePuncturedEvent>(OnPuncture);
            Subscribe<OvertakeEvent>(OnOvertake);
            Subscribe<FuelDepletedEvent>(OnFuelOut);
            Subscribe<CarOffTrackEvent>(OnOffTrack);
            Subscribe<EpisodeEndedEvent>(OnEpisodeEnded);
            Subscribe<CarRewardSnapshotEvent>(OnRewardSnapshot);
            Subscribe<DriverPersonalityChangedEvent>(OnPersonalityChanged);
            // Race boundary = circuit boundary. Without this, samples from
            // cars respawned into the next procedural circuit accumulate
            // under the previous circuit's race record and the viewer plots
            // them in stale bbox coords.
            Subscribe<CircuitRegeneratedEvent>(OnCircuitRegenerated);
            // Race-scoped strict boundaries. When active these take over from
            // the counter-gate + watchdog + CircuitRegen hard-flush logic
            // below — every race record begins on RaceStartedEvent and
            // flushes on RaceEndedEvent with the final outcome stamped in.
            Subscribe<RaceStartedEvent>(OnRaceStarted);
            Subscribe<RaceEndedEvent>(OnRaceEnded);
            Subscribe<DriverFinishedRaceEvent>(OnDriverFinishedRace);
        }

        private bool IsRaceScoped => _coord != null && _coord.IsRaceScoped;

        public RaceRecordDto CurrentInFlight => _inFlight;

        public void BeginRace(RaceContext context)
        {
            ResetInFlight();
            _raceStartUtc = DateTime.UtcNow;
            _inFlight = new RaceRecordDto
            {
                race_id = Guid.NewGuid().ToString("N"),
                captured_at_utc = _raceStartUtc.ToString("o"),
                env_pid = context.EnvPid != 0 ? context.EnvPid : SafePid(),
                episode_index = context.EpisodeIndex,
                sample_hz = SampleHz,
                circuit = new CircuitInfoDto
                {
                    id = context.CircuitId ?? string.Empty,
                    length_m = context.CircuitLengthM,
                    piece_count = context.CircuitPieceCount,
                },
                duration_s = 0f,
                drivers = new List<DriverRaceRecordDto>(),
                events = new List<RaceEventDto>(),
            };
        }

        public void EndRaceHint()
        {
            // No-op: the recorder closes on EpisodeEndedEvent once all
            // known drivers have ended. The hint exists so future
            // explicit-end orchestration can call in without changing
            // the implementation.
        }

        // ----- handlers -----

        private void OnSpawn(CarSpawnedEvent e)
        {
            EnsureRaceOpen();
            GetOrAddDriver(e.Id);
        }

        private void OnDespawn(CarDespawnedEvent e)
        {
            // Episode-end carries the terminal state; despawn alone is not
            // a finalisation signal (could be a scene-level cleanup).
        }

        private void OnTick(CarPhysicsTickedEvent e)
        {
            // Cheap stride check; watchdog finalizes a stuck race so the
            // dict + sample lists don't grow forever.
            if ((++_watchdogTickCounter & (WatchdogTickStride - 1)) == 0)
                MaybeWatchdogFlush();

            EnsureRaceOpen();
            var d = GetOrAddDriver(e.Id);
            if (d.Ended) return;

            d.LocalTime += e.Dt;
            d.LastArcLength = e.ArcLengthAlong;
            d.LastPosition = e.Position;
            d.LastSpeed = e.Speed;

            // Float accumulation of 50 × (1/50) drifts below 1.0 in single
            // precision. The same accumulation drift applies at 5 Hz (10
            // ticks per period). Use a half-Dt epsilon so emits land at the
            // intended ticks regardless of period.
            float epsilon = e.Dt * 0.5f;
            while (d.LocalTime + epsilon >= d.NextSampleTime)
            {
                EmitSample(e.Id, d, e.Position, e.Heading, e.Speed, e.ArcLengthAlong, d.NextSampleTime);
                d.NextSampleTime += SamplePeriod;
            }
            if (d.LocalTime > _maxLocalTime) _maxLocalTime = d.LocalTime;
        }

        private void EmitSample(CarId id, DriverInFlight d, Vector3 pos, float heading, float speed, float arc, float labelT)
        {
            float lapFrac = 0f;
            if (_inFlight != null && _inFlight.circuit != null && _inFlight.circuit.length_m > 0f)
                lapFrac = Mathf.Clamp01(arc / _inFlight.circuit.length_m);

            // 3 sectors: floor(lap_frac * 3), capped at 2 so lap_frac=1.0 doesn't overflow to 3.
            int sector = Mathf.Clamp((int)(lapFrac * 3f), 0, 2);

            var sample = new DriverSampleDto
            {
                t = labelT,
                lap = d.LastLap,
                lap_frac = lapFrac,
                sector = sector,
                speed = speed,
                heading = heading,
                fuel_l = _fuel != null ? _fuel.Get(id).Liters : 0f,
                tire_l = _tires != null ? _tires.Get(id).LeftWear : 0f,
                tire_r = _tires != null ? _tires.Get(id).RightWear : 0f,
                draft = _draft != null ? _draft.Get(id).Strength : 0f,
                reward_cum = 0f, // RewardShaper has no per-tick publish today; populated only at end_state.
                pos = pos,
            };
            d.Dto.samples.Add(sample);
            // Newest-wins ring trim. Amortized O(1) by batching the
            // RemoveRange call once we drift SampleTrimSlack beyond the cap.
            int overflow = d.Dto.samples.Count - MaxSamplesPerDriver;
            if (overflow >= SampleTrimSlack)
                d.Dto.samples.RemoveRange(0, overflow);
        }

        private void OnLap(CarLapCompletedEvent e)
        {
            var d = GetOrAddDriver(e.Id);
            d.LastLap = e.LapNumber;
            d.Dto.lap_times_s.Add(e.LapTimeSeconds);
            _inFlight?.events.Add(new RaceEventDto
            {
                t = d.LocalTime,
                type = "lap_complete",
                car_id = e.Id.Value,
                lap = e.LapNumber,
                lap_time = e.LapTimeSeconds,
            });
        }

        private void OnWallHit(CarHitWallEvent e)
        {
            var d = GetOrAddDriver(e.Id);
            d.Dto.end_state.wall_hits++;
            _inFlight?.events.Add(new RaceEventDto
            {
                t = d.LocalTime,
                type = "wall_hit",
                car_id = e.Id.Value,
                impact_speed = e.ImpactSpeed,
            });
        }

        private void OnCarHit(CarHitCarEvent e)
        {
            // Only count hits for cars already known via spawn/tick — refusing
            // to auto-create from passive events prevents phantom records when
            // RaceState (or any other publisher) leaks stale CarIds across a
            // circuit boundary.
            if (!_drivers.TryGetValue(e.A, out var a)) return;
            if (!_drivers.TryGetValue(e.B, out var b)) return;
            a.Dto.end_state.car_hits++;
            b.Dto.end_state.car_hits++;
            _inFlight?.events.Add(new RaceEventDto
            {
                t = Mathf.Max(a.LocalTime, b.LocalTime),
                type = "car_hit",
                a = e.A.Value,
                b = e.B.Value,
                impact_speed = e.ImpactSpeed,
            });
        }

        private void OnPuncture(TirePuncturedEvent e)
        {
            var d = GetOrAddDriver(e.Id);
            if (e.Side == TireSide.Left) d.Dto.end_state.punctured_l = true;
            else d.Dto.end_state.punctured_r = true;
            _inFlight?.events.Add(new RaceEventDto
            {
                t = d.LocalTime,
                type = "puncture",
                car_id = e.Id.Value,
                side = e.Side.ToString(),
                wear_at_puncture = e.WearAtPuncture,
            });
        }

        private void OnOvertake(OvertakeEvent e)
        {
            // Same guard as OnCarHit: refuse to materialise phantom drivers
            // from a passive event. Pairs with the CircuitRegen reset in
            // RaceStateService — belt + braces against stale-id publishers.
            if (!_drivers.TryGetValue(e.Passer, out var passer)) return;
            if (!_drivers.TryGetValue(e.Passed, out var passed)) return;
            passer.Dto.end_state.overtakes_made++;
            passed.Dto.end_state.overtakes_lost++;
            _inFlight?.events.Add(new RaceEventDto
            {
                t = passer.LocalTime,
                type = "overtake",
                passer = e.Passer.Value,
                passed = e.Passed.Value,
                new_position = e.NewPosition,
            });
        }

        private void OnFuelOut(FuelDepletedEvent e)
        {
            var d = GetOrAddDriver(e.Id);
            _inFlight?.events.Add(new RaceEventDto
            {
                t = d.LocalTime,
                type = "fuel_out",
                car_id = e.Id.Value,
            });
        }

        private void OnOffTrack(CarOffTrackEvent e)
        {
            var d = GetOrAddDriver(e.Id);
            _inFlight?.events.Add(new RaceEventDto
            {
                t = d.LocalTime,
                type = "off_track",
                car_id = e.Id.Value,
            });
        }

        private void OnRewardSnapshot(CarRewardSnapshotEvent e)
        {
            // Refuse to mint phantom records from a passive event — same rule
            // as OnOvertake / OnCarHit. Real drivers reach this only after
            // OnSpawn/OnTick already added them.
            if (!_drivers.TryGetValue(e.Car, out var d)) return;
            d.LastCumulativeReward = e.Cumulative;
        }

        private void OnPersonalityChanged(DriverPersonalityChangedEvent e)
        {
            EnsureRaceOpen();
            var d = GetOrAddDriver(e.Id);
            e.Personality.WriteTo(d.Dto.personality);
        }

        private void OnCircuitRegenerated(CircuitRegeneratedEvent e)
        {
            // Race-scoped: the coordinator's RaceStartedEvent/RaceEndedEvent
            // own race boundaries. CircuitRegen always lands between races
            // (TrainingDirector regen runs in OnRaceEnded), so the in-flight
            // record is already null — just refresh the circuit context for
            // the next BeginRace call.
            if (IsRaceScoped)
            {
                _pendingCircuitContext = new RaceContext(
                    _processEpisodeCounter,
                    e.CircuitId,
                    e.LengthM,
                    e.PieceCount,
                    SafePid());
                return;
            }

            // Legacy hard-flush: counter-gate may not have fired yet but the
            // car positions are about to teleport onto a new geometry, so
            // close the in-flight record now and reopen against the new
            // circuit.
            if (_inFlight != null && _drivers.Count > 0) FinalizeAndFlush();
            else ResetInFlight();
            BeginRace(new RaceContext(
                _processEpisodeCounter,
                e.CircuitId,
                e.LengthM,
                e.PieceCount,
                SafePid()));
        }

        // Pending circuit metadata staged by OnCircuitRegenerated in
        // race-scoped mode. Consumed by the next OnRaceStarted so each
        // record carries the geometry it actually ran on.
        private RaceContext? _pendingCircuitContext;

        private void OnRaceStarted(RaceStartedEvent e)
        {
            if (!IsRaceScoped) return;
            // Race-scoped strict boundary: discard anything in-flight (no
            // double-flush — the previous race already ended via OnRaceEnded)
            // and open a fresh record stamped with the latest circuit context.
            ResetInFlight();
            var ctx = _pendingCircuitContext ?? BuildAutoContext();
            BeginRace(ctx);
            if (_inFlight != null)
            {
                _inFlight.end_reason = "in_progress";
                _inFlight.lap_target = e.LapTarget;
            }
        }

        private void OnRaceEnded(RaceEndedEvent e)
        {
            if (!IsRaceScoped) return;
            if (_inFlight == null) return;

            _inFlight.end_reason = e.Reason.ToString();
            _inFlight.finishers_count = e.FinishersCount;
            _inFlight.eliminated_count = e.EliminatedCount;
            FinalizeAndFlush();
        }

        private void OnDriverFinishedRace(DriverFinishedRaceEvent e)
        {
            if (!IsRaceScoped) return;
            if (!_drivers.TryGetValue(e.CarId, out var d)) return;
            d.Dto.end_state.finish_position = e.FinishPosition;
            d.Dto.end_state.total_race_time_s = e.TotalRaceTimeS;
            d.Dto.end_state.laps_completed = e.LapsCompleted;
            // Tag the driver as finished BEFORE the race-level flush so
            // FinalizeAndFlush's "still-mid-episode" backfill loop skips it.
            d.Ended = true;
            _endedThisRound++;
            _inFlight?.events.Add(new RaceEventDto
            {
                t = d.LocalTime,
                type = "driver_finished",
                car_id = e.CarId.Value,
                new_position = e.FinishPosition,
                lap = e.LapsCompleted,
                lap_time = e.TotalRaceTimeS,
            });
        }

        private void OnEpisodeEnded(EpisodeEndedEvent e)
        {
            _processEpisodeCounter++;
            if (_inFlight == null) return;

            var d = GetOrAddDriver(e.Car);
            if (!d.Ended) _endedThisRound++; // double-end on the same CarId would be a bug, but guard anyway
            d.Ended = true;
            var es = d.Dto.end_state;
            es.reason = e.Reason.ToString();
            es.laps_completed = e.LapsCompleted;
            es.cumulative_reward = e.CumulativeReward;
            if (_fuel != null)
            {
                var f = _fuel.Get(e.Car);
                es.fuel_l_final = f.Liters;
                if (d.Dto.samples.Count > 0)
                    es.fuel_l_start = d.Dto.samples[0].fuel_l;
            }
            if (_tires != null)
            {
                var t = _tires.Get(e.Car);
                es.tire_l_final = t.LeftWear;
                es.tire_r_final = t.RightWear;
                if (t.LeftPunctured) es.punctured_l = true;
                if (t.RightPunctured) es.punctured_r = true;
            }
            if (_raceState != null)
                es.final_position = _raceState.GetPosition(e.Car);

            // Counter gate: every CarSpawn → episode_end is one driver done.
            // Stale CarIds left in _drivers from prior rounds are irrelevant
            // because the counter resets in FinalizeAndFlush.
            //
            // Race-scoped mode: the coordinator's RaceEndedEvent is the only
            // flush trigger. Per-car episode-ends still populate end_state
            // (above) so finalize sees the full driver picture, but we never
            // flush from this counter — every driver's episode_end arrives in
            // a brief window at race-end and a flush halfway through that
            // window would split one race across two files.
            if (IsRaceScoped) return;
            if (_endedThisRound >= _expectedDriversPerRound) FinalizeAndFlush();
        }

        // ----- watchdog -----

        // Force-flush guard. Counter-gate may never fire if a respawn
        // skips EpisodeEndedEvent or expectedDriversPerRound is
        // miscalibrated; without this the in-flight race + _drivers dict
        // accumulate forever and the process slowly OOMs (~hours into an
        // unattended training session, exactly the user-observed
        // symptom). Triggers on wall-clock age, driver-dict size, or
        // event-list size.
        private void MaybeWatchdogFlush()
        {
            if (_inFlight == null) return;
            // Race-scoped mode: the coordinator's MaxRaceSteps cap forces
            // race resolution at the simulation layer and triggers a clean
            // RaceEndedEvent → flush. The wall-clock watchdog here would
            // race that signal and could split a long race into two records.
            if (IsRaceScoped) return;

            // Trim events in-place first — cheaper than a flush, and
            // covers the common case of a race that's just verbose, not
            // stuck.
            if (_inFlight.events != null && _inFlight.events.Count > MaxEventsPerRace + SampleTrimSlack)
            {
                int drop = _inFlight.events.Count - MaxEventsPerRace;
                _inFlight.events.RemoveRange(0, drop);
            }

            var now = _clock();
            double age = (now - _raceStartUtc).TotalSeconds;
            bool overTime = age > WatchdogMaxRaceSeconds;
            bool overDrivers = _drivers.Count > _expectedDriversPerRound * WatchdogDriverMultiple;
            bool overEvents = _inFlight.events != null && _inFlight.events.Count > MaxEventsPerRace * 2;

            if (overTime || overDrivers || overEvents)
            {
                Debug.LogWarning(
                    $"[RaceTelemetry] watchdog flush age={age:F1}s drivers={_drivers.Count} events={_inFlight.events?.Count ?? 0} ended={_endedThisRound}/{_expectedDriversPerRound}");
                FinalizeAndFlush();
            }
        }

        // ----- finalisation -----

        private void FinalizeAndFlush()
        {
            if (_inFlight == null) return;
            _inFlight.duration_s = _maxLocalTime;

            // Cars still mid-episode at flush time (watchdog timeout, or
            // CircuitRegen boundary) never received EpisodeEndedEvent, so
            // their end_state stayed zero — viewer rendered "?" with 0
            // reward / 0 laps / 0 fuel. Populate from live services so every
            // driver in the record carries real data.
            foreach (var kv in _drivers)
            {
                var d = kv.Value;
                if (d.Ended) continue;
                var carId = kv.Key;
                var es = d.Dto.end_state;
                // A car still mid-episode that already banked at least one lap
                // is succeeding, not failing — bucket it with Success so the
                // viewer's success panel includes long-running good drivers.
                // No-lap cars stay tagged "cut_at_flush" for distinction.
                es.reason = d.LastLap > 0
                    ? EpisodeEndReason.Success.ToString()
                    : "cut_at_flush";
                es.laps_completed = d.LastLap;
                es.cumulative_reward = d.LastCumulativeReward;
                if (_fuel != null)
                {
                    var f = _fuel.Get(carId);
                    es.fuel_l_final = f.Liters;
                    if (d.Dto.samples.Count > 0)
                        es.fuel_l_start = d.Dto.samples[0].fuel_l;
                }
                if (_tires != null)
                {
                    var t = _tires.Get(carId);
                    es.tire_l_final = t.LeftWear;
                    es.tire_r_final = t.RightWear;
                    if (t.LeftPunctured) es.punctured_l = true;
                    if (t.RightPunctured) es.punctured_r = true;
                }
                if (_raceState != null)
                    es.final_position = _raceState.GetPosition(carId);

                // Dashboard's end-of-episode reason breakdown reads from
                // TrainingTelemetry's episode_end JSONL stream. Cars that
                // never received EpisodeEndedEvent never emitted there, so
                // long-running lapping drivers never tallied as Success.
                // Mirror the synthesized end_state into the same sink so the
                // breakdown counts cut-at-flush Success cars too.
                float lapFrac = 0f;
                if (_inFlight.circuit != null && _inFlight.circuit.length_m > 0f)
                    lapFrac = Mathf.Clamp01(d.LastArcLength / _inFlight.circuit.length_m);
                TrainingTelemetry.EmitEpisodeEnd(
                    carIdHash: carId.Value,
                    circuit: _inFlight.circuit?.id ?? string.Empty,
                    endReason: es.reason,
                    endX: d.LastPosition.x,
                    endZ: d.LastPosition.z,
                    lapFraction: lapFrac,
                    lapsCompleted: d.LastLap,
                    steps: 0,
                    elapsedSec: d.LocalTime,
                    cumulativeReward: d.LastCumulativeReward,
                    wallHitCount: es.wall_hits);
            }

            // Wall-clock throttle: with WriteIntervalSeconds=0 (default)
            // every complete round flushes. Bump to drop intermediate rounds
            // on disk-fill. The very first race after process start is
            // always written (gate initialised on the fly).
            var now = _clock();
            if (_nextEligibleWriteUtc == DateTime.MinValue) _nextEligibleWriteUtc = now;
            if (now >= _nextEligibleWriteUtc)
            {
                if (_sink != null) _sink.WriteRace(_inFlight);
                _nextEligibleWriteUtc = now.AddSeconds(WriteIntervalSeconds);
            }

            ResetInFlight();
        }

        // ----- helpers -----

        private void EnsureRaceOpen()
        {
            if (_inFlight != null) return;
            BeginRace(BuildAutoContext());
        }

        private RaceContext BuildAutoContext()
        {
            int pid = SafePid();
            string circuit = TrainingTelemetryContext.LastCircuitId ?? string.Empty;
            return new RaceContext(_processEpisodeCounter, circuit, 0f, 0, pid);
        }

        private DriverInFlight GetOrAddDriver(CarId id)
        {
            if (_drivers.TryGetValue(id, out var existing)) return existing;
            EnsureRaceOpen();
            var personality = new float[DriverPersonality.ObservationLength];
            DriverPersonality.Default.WriteTo(personality);
            var dto = new DriverRaceRecordDto
            {
                car_id = id.Value,
                display_name = $"car_{id.Value}",
                personality = personality,
                end_state = new EndStateCardDto
                {
                    reason = string.Empty,
                    final_position = 0,
                },
                samples = new List<DriverSampleDto>(),
                lap_times_s = new List<float>(),
            };
            _inFlight.drivers.Add(dto);
            var rec = new DriverInFlight { Dto = dto };
            _drivers[id] = rec;
            return rec;
        }

        private void ResetInFlight()
        {
            _inFlight = null;
            _drivers.Clear();
            _maxLocalTime = 0f;
            _endedThisRound = 0;
        }

        private static int SafePid()
        {
            try { return System.Diagnostics.Process.GetCurrentProcess().Id; }
            catch { return 0; }
        }

        private sealed class DriverInFlight
        {
            public DriverRaceRecordDto Dto;
            public float LocalTime;
            public float NextSampleTime = SamplePeriod;
            public int LastLap;
            public float LastArcLength;
            public Vector3 LastPosition;
            public float LastSpeed;
            public float LastCumulativeReward;
            public bool Ended;
        }
    }
}
