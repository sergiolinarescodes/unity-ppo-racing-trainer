using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.AiDriver.Physics;
using UnityPpoRacingTrainer.Core.AiDriver.Training;
using UnityPpoRacingTrainer.Core.Terrain.Scenarios;
using UnityPpoRacingTrainer.Core.Track.Loop;
using Reflex.Core;
using Unidad.Core.Abstractions;
using Unidad.Core.Bootstrap;
using Unidad.Core.EventBus;
using Unidad.Core.Systems;
using Unidad.Core.Testing;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.AiDriver.Race
{
    public interface IRaceStateService
    {
        int GetPosition(CarId carId);
        IReadOnlyList<CarId> OrderedCars { get; }
    }

    /// <summary>
    /// Tracks the live finishing order of every active car using
    /// <c>laps * 1000 + arcLengthAlong</c> as the scalar progress metric. Each
    /// time the ordering changes, fires <see cref="OvertakeEvent"/> for every
    /// pair that swapped. Used by both the bookmaker layer (live position UI,
    /// bet resolution) and the personality-aware reward shaping (overtake bonus).
    /// </summary>
    internal sealed class RaceStateService : SystemServiceBase, IRaceStateService
    {
        // Recompute on every Nth tick event. With ~10 cars × 50 Hz = 500
        // events/sec/env, N=5 yields ~100 Hz refresh — plenty for live UI
        // and overtake detection, two orders of magnitude cheaper than the
        // old per-event sort.
        private const int RecomputeEveryNEvents = 5;

        private readonly Dictionary<CarId, Progress> _progress = new();
        private readonly Dictionary<CarId, int> _previousPosition = new();
        private readonly Dictionary<int, CarId> _previousByPosition = new();
        private readonly List<CarId> _orderedScratch = new();
        private readonly List<CarId> _ordered = new();
        private int _tickEventCounter;

        public RaceStateService(IEventBus eventBus) : base(eventBus)
        {
            Subscribe<CarPhysicsTickedEvent>(OnTick);
            Subscribe<CarLapCompletedEvent>(OnLap);
            Subscribe<CarDespawnedEvent>(e =>
            {
                _progress.Remove(e.Id);
                if (_previousPosition.TryGetValue(e.Id, out var pos))
                {
                    _previousPosition.Remove(e.Id);
                    _previousByPosition.Remove(pos);
                }
                Recompute();
            });
            // Race boundary = circuit boundary. ML-Agents respawns between
            // procedural circuits don't reliably fire CarDespawnedEvent for
            // every CarId, so progress dicts accumulate stale ids. The next
            // Recompute then publishes OvertakeEvent for dead cars, which
            // RaceTelemetryService materialises as phantom driver records
            // ("?" name, zero stats, but non-zero overtake/car_hit counts).
            Subscribe<CircuitRegeneratedEvent>(_ =>
            {
                _progress.Clear();
                _previousPosition.Clear();
                _previousByPosition.Clear();
                _ordered.Clear();
                _tickEventCounter = 0;
            });
        }

        public IReadOnlyList<CarId> OrderedCars => _ordered;

        public int GetPosition(CarId carId)
        {
            for (int i = 0; i < _ordered.Count; i++)
                if (_ordered[i].Value == carId.Value) return i + 1;
            return 0;
        }

        private void OnTick(CarPhysicsTickedEvent e)
        {
            var p = _progress.TryGetValue(e.Id, out var prev) ? prev : default;
            p.Arc = e.ArcLengthAlong;
            _progress[e.Id] = p;

            if (++_tickEventCounter >= RecomputeEveryNEvents)
            {
                _tickEventCounter = 0;
                Recompute();
            }
        }

        private void OnLap(CarLapCompletedEvent e)
        {
            var p = _progress.TryGetValue(e.Id, out var prev) ? prev : default;
            p.Laps = e.LapNumber;
            // Arc resets at lap line — leave to next tick to refresh.
            _progress[e.Id] = p;
            // Lap boundaries are the most race-relevant ordering events;
            // always force a fresh sort here regardless of the tick budget.
            _tickEventCounter = 0;
            Recompute();
        }

        private void Recompute()
        {
            _orderedScratch.Clear();
            foreach (var id in _progress.Keys) _orderedScratch.Add(id);
            _orderedScratch.Sort((a, b) =>
            {
                _progress.TryGetValue(a, out var pa);
                _progress.TryGetValue(b, out var pb);
                float ta = pa.Laps * 1_000_000f + pa.Arc;
                float tb = pb.Laps * 1_000_000f + pb.Arc;
                return tb.CompareTo(ta);
            });

            // Detect swaps via O(1) reverse-map lookup instead of an inner
            // scan over _previousPosition.
            for (int i = 0; i < _orderedScratch.Count; i++)
            {
                var id = _orderedScratch[i];
                int newPos = i + 1;
                int oldPos = _previousPosition.TryGetValue(id, out var p) ? p : newPos;
                if (newPos < oldPos
                    && _previousByPosition.TryGetValue(newPos, out var passed)
                    && passed.Value != id.Value)
                {
                    Publish(new OvertakeEvent(id, passed, newPos));
                }
            }

            _previousPosition.Clear();
            _previousByPosition.Clear();
            for (int i = 0; i < _orderedScratch.Count; i++)
            {
                var id = _orderedScratch[i];
                int pos = i + 1;
                _previousPosition[id] = pos;
                _previousByPosition[pos] = id;
            }

            _ordered.Clear();
            _ordered.AddRange(_orderedScratch);
        }

        private struct Progress { public int Laps; public float Arc; }
    }

    // ========================================================================
    // RaceCoordinator — race-scoped episode lifecycle.
    //
    // Co-located in this existing file rather than its own RaceCoordinator.cs
    // so the new types land in the already-tracked .csproj without waiting
    // for a Unity refresh (per feedback_unity_new_cs_files.md: new .cs files
    // break dotnet build until Unity regenerates the project).
    // ========================================================================

    /// <summary>
    /// Per-environment race coordinator. Owns the lifecycle of a single "race"
    /// — one episode for every registered driver that runs from spawn through
    /// a fixed lap target (default 3) or full elimination. The race-scoped
    /// flow is the default; the supervisor can override with the
    /// RACING_RACE_SCOPED env var ("0" = per-car terminals, legacy mode).
    ///
    /// Activation is decided lazily on every <see cref="IsRaceScoped"/> read so
    /// env-var flips take effect at the next coordinator decision without
    /// restarting the trainer. Within a single race the value is latched
    /// (cached at <c>BeginNewRace</c>).
    /// </summary>
    public interface IRaceCoordinator
    {
        bool IsRaceScoped { get; }
        int LapTarget { get; }
        int MaxRaceSteps { get; }
        string CurrentRaceId { get; }
        RaceLifecycleState State { get; }
        DriverRaceState GetDriverState(CarId carId);
        bool ShouldEndCarEpisode(CarId carId);
        void AcknowledgeEpisodeEnd(CarId carId);
        int GetFinishersCount();
        int GetEliminatedCount();
        int GetDriverCount();
    }

    public enum RaceLifecycleState
    {
        Idle,
        Active,
        Ended,
    }

    public enum DriverRaceState
    {
        Racing,
        Finished,
        Eliminated,
    }

    public enum RaceEndReason
    {
        AllDriversResolved,
        MaxStepsCap,
        Aborted,
        // First finisher crossed lap_target, but the dynamic shrinking
        // countdown for the next finisher elapsed before all remaining
        // racers either finished or were eliminated. Force-eliminates the
        // stragglers so the race actually closes instead of stalling on the
        // slowest driver.
        PostFinishTimeout,
    }

    /// <summary>
    /// Fired when the coordinator opens a new race window (first driver
    /// spawns into Idle/Ended state). Telemetry uses this as the canonical
    /// race-start boundary in race-scoped mode.
    /// </summary>
    public readonly record struct RaceStartedEvent(
        string RaceId,
        int LapTarget,
        int DriverCount);

    /// <summary>
    /// Fired exactly once per race when every registered driver has
    /// either finished or been eliminated, the max-step cap fires, or the
    /// loop opens mid-race. All AiDriverAgentBehaviours call EndEpisode
    /// on the next OnActionReceived after this event arrives.
    /// </summary>
    public readonly record struct RaceEndedEvent(
        string RaceId,
        int FinishersCount,
        int EliminatedCount,
        RaceEndReason Reason);

    /// <summary>
    /// Published by <c>EpisodeRunner</c> when a driver wrecks / off-tracks /
    /// fuels-out / hits the wall-cap in race-scoped mode. Coordinator marks
    /// driver Eliminated; the PPO episode for that car stays open until the
    /// race ends.
    /// </summary>
    public readonly record struct DriverEliminatedEvent(
        CarId CarId,
        EpisodeEndReason Reason);

    /// <summary>
    /// Published by <c>EpisodeRunner</c> when a driver completes the race
    /// lap target. Coordinator marks driver Finished; PPO episode stays
    /// open until the race ends so the policy still receives the
    /// post-finish-line state distribution.
    /// </summary>
    public readonly record struct DriverFinishedRaceEvent(
        CarId CarId,
        int FinishPosition,
        float TotalRaceTimeS,
        int LapsCompleted);

    internal sealed class RaceCoordinator : SystemServiceBase, IRaceCoordinator, ITickable
    {
        private const int DefaultLapTarget = 3;
        // Hard ceiling on per-race wall clock (18000 sim ticks ≈ 6 minutes
        // at 50 Hz). Sized to accommodate 3-lap races on long procedural
        // circuits at the canonical acc/brake pace. Still the slowest-driver
        // backstop — race force-resolves all still-Racing drivers as
        // Failure_Timeout when this fires. Engaged only when zero drivers
        // ever finish; once at least one driver crosses the lap target the
        // post-finish shrinking countdown takes over.
        private const int DefaultMaxRaceSteps = 18000;

        // Sim tick rate (fixed update). Matches the 50 Hz reference baked into
        // the dashboard's elapsed-from-steps conversion. Kept local so the
        // post-finish budgets stay expressed in seconds.
        private const int SimHz = 50;

        // Post-first-finish dynamic countdown — once the lead driver crosses
        // the lap target the coordinator stops waiting indefinitely on the
        // slowest car. Schedule of stragglers-wait windows:
        //   after finisher 1 → 60 s window for next finisher
        //   after finisher 2 → 50 s
        //   after finisher 3 → 40 s
        //   after finisher 4 onward → 30 s (floor)
        // The clock is reset to the new (smaller) value each time a driver
        // finishes, NOT each time a driver is eliminated. If the window
        // elapses with still-racing drivers, they are force-eliminated and
        // the race ends as PostFinishTimeout. Replaces the previous behaviour
        // where the whole race had to wait the full MaxRaceSteps cap after
        // the lead car finished.
        private static readonly int[] PostFinishBudgetSecondsByFinisherIndex =
            new[] { 60, 50, 40, 30 };

        private readonly Func<float> _envOverride;
        private readonly Func<float> _lapTargetOverride;
        private readonly ITimeProvider _time;

        private string _currentRaceId = string.Empty;
        private RaceLifecycleState _state = RaceLifecycleState.Idle;
        private bool _latchedScopedForRace;
        private int _latchedLapTarget = DefaultLapTarget;
        private int _stepsSinceStart;
        private int _finishersCount;
        private int _eliminatedCount;
        private float _raceStartSimTime;
        private readonly Dictionary<CarId, DriverRaceState> _drivers = new();
        private readonly Dictionary<CarId, int> _finishOrder = new();
        private readonly HashSet<CarId> _pendingEnd = new();
        private int _nextFinishPosition = 1;
        // -1 = no driver has finished yet (countdown inactive). >=0 = remaining
        // sim steps the coordinator will wait for the next finisher before
        // force-eliminating the still-racing tail.
        private int _postFinishStepsRemaining = -1;

        public int LapTarget => _latchedLapTarget;
        public int MaxRaceSteps { get; set; } = DefaultMaxRaceSteps;
        public string CurrentRaceId => _currentRaceId;
        public RaceLifecycleState State => _state;

        // IsRaceScoped is the live env probe; the per-race behaviour caches
        // this value at BeginNewRace into _latchedScopedForRace so an in-flight
        // race never tears if the env var is flipped mid-run. The supervisor
        // exports RACING_RACE_SCOPED ("1" = race-scoped, "0" = per-car
        // terminals); absent / unparseable, the coordinator defaults to
        // race-scoped (the trained Latest behaviour).
        public bool IsRaceScoped
        {
            get
            {
                float ovr = _envOverride?.Invoke() ?? float.NaN;
                if (!float.IsNaN(ovr)) return ovr >= 0.5f;
                return true;
            }
        }

        public RaceCoordinator(
            IEventBus bus,
            ITimeProvider time = null,
            Func<float> envOverride = null,
            Func<float> lapTargetOverride = null) : base(bus)
        {
            _time = time;
            _envOverride = envOverride;
            _lapTargetOverride = lapTargetOverride;

            Subscribe<CarSpawnedEvent>(OnCarSpawned);
            Subscribe<CarDespawnedEvent>(OnCarDespawned);
            Subscribe<DriverEliminatedEvent>(OnDriverEliminated);
            Subscribe<DriverFinishedRaceEvent>(OnDriverFinished);
            Subscribe<LoopOpenedEvent>(_ => AbortRace());
        }

        public DriverRaceState GetDriverState(CarId carId)
            => _drivers.TryGetValue(carId, out var s) ? s : DriverRaceState.Racing;

        public bool ShouldEndCarEpisode(CarId carId) => _pendingEnd.Contains(carId);

        public void AcknowledgeEpisodeEnd(CarId carId)
        {
            _pendingEnd.Remove(carId);
            _drivers.Remove(carId);
            _finishOrder.Remove(carId);
            // Once the last car has ack'd, drop to Idle so the next spawn
            // opens a fresh race window.
            if (_state == RaceLifecycleState.Ended && _drivers.Count == 0 && _pendingEnd.Count == 0)
            {
                _state = RaceLifecycleState.Idle;
                _finishersCount = 0;
                _eliminatedCount = 0;
                _nextFinishPosition = 1;
                _stepsSinceStart = 0;
            }
        }

        public int GetFinishersCount() => _finishersCount;
        public int GetEliminatedCount() => _eliminatedCount;
        public int GetDriverCount() => _drivers.Count;

        public void Tick(float deltaTime)
        {
            if (!_latchedScopedForRace || _state != RaceLifecycleState.Active) return;
            _stepsSinceStart++;

            // Post-finish shrinking countdown: once active, it is the
            // tightest cap on the race tail. Race force-closes here long
            // before the absolute MaxRaceSteps backstop fires.
            if (_postFinishStepsRemaining > 0)
            {
                _postFinishStepsRemaining--;
                if (_postFinishStepsRemaining == 0)
                {
                    ForceTimeoutEliminateRacingDrivers();
                    EndRace(RaceEndReason.PostFinishTimeout);
                    return;
                }
            }

            if (_stepsSinceStart >= MaxRaceSteps)
            {
                ForceTimeoutEliminateRacingDrivers();
                EndRace(RaceEndReason.MaxStepsCap);
            }
        }

        private void OnCarSpawned(CarSpawnedEvent e)
        {
            if (!IsRaceScoped) return;
            if (_state == RaceLifecycleState.Idle || _state == RaceLifecycleState.Ended)
                BeginNewRace();
            if (!_drivers.ContainsKey(e.Id)) _drivers[e.Id] = DriverRaceState.Racing;
        }

        private void OnCarDespawned(CarDespawnedEvent e)
        {
            // Despawn alone is not a race termination; AcknowledgeEpisodeEnd
            // handles cleanup at PPO episode boundaries.
        }

        private void OnDriverEliminated(DriverEliminatedEvent e)
        {
            if (!_latchedScopedForRace) return;
            if (!_drivers.TryGetValue(e.CarId, out var s)) return;
            if (s != DriverRaceState.Racing) return;
            _drivers[e.CarId] = DriverRaceState.Eliminated;
            _eliminatedCount++;
            MaybeEndRace();
        }

        private void OnDriverFinished(DriverFinishedRaceEvent e)
        {
            if (!_latchedScopedForRace) return;
            if (!_drivers.TryGetValue(e.CarId, out var s)) return;
            if (s != DriverRaceState.Racing) return;
            _drivers[e.CarId] = DriverRaceState.Finished;
            if (!_finishOrder.ContainsKey(e.CarId))
                _finishOrder[e.CarId] = _nextFinishPosition++;
            _finishersCount++;
            // Reset the dynamic shrinking countdown for the next finisher.
            // Per the schedule the budget shrinks 60→50→40→30 s and floors at
            // 30 s for any subsequent finisher. Reset (not accumulate) — every
            // line crossing pulls the deadline forward.
            _postFinishStepsRemaining = NextPostFinishBudgetSteps(_finishersCount);
            MaybeEndRace();
        }

        private static int NextPostFinishBudgetSteps(int finishersSoFar)
        {
            int idx = Mathf.Max(0, finishersSoFar - 1);
            int seconds = idx < PostFinishBudgetSecondsByFinisherIndex.Length
                ? PostFinishBudgetSecondsByFinisherIndex[idx]
                : PostFinishBudgetSecondsByFinisherIndex[PostFinishBudgetSecondsByFinisherIndex.Length - 1];
            return seconds * SimHz;
        }

        public int GetFinishPosition(CarId carId)
            => _finishOrder.TryGetValue(carId, out var pos) ? pos : 0;

        private void MaybeEndRace()
        {
            if (_state != RaceLifecycleState.Active) return;
            int total = _drivers.Count;
            if (total == 0) return;
            if ((_finishersCount + _eliminatedCount) >= total)
                EndRace(RaceEndReason.AllDriversResolved);
        }

        private void BeginNewRace()
        {
            _currentRaceId = Guid.NewGuid().ToString("N");
            _state = RaceLifecycleState.Active;
            _latchedScopedForRace = IsRaceScoped;
            _stepsSinceStart = 0;
            _finishersCount = 0;
            _eliminatedCount = 0;
            _nextFinishPosition = 1;
            _drivers.Clear();
            _finishOrder.Clear();
            _pendingEnd.Clear();
            _postFinishStepsRemaining = -1;
            _raceStartSimTime = _time?.Time ?? 0f;

            // Resolve lap target with env override / default. Read once per race.
            float lapOvr = _lapTargetOverride?.Invoke() ?? float.NaN;
            _latchedLapTarget = !float.IsNaN(lapOvr) && lapOvr > 0f
                ? Mathf.Max(1, Mathf.RoundToInt(lapOvr))
                : DefaultLapTarget;

            Publish(new RaceStartedEvent(_currentRaceId, _latchedLapTarget, 0));
        }

        private void ForceTimeoutEliminateRacingDrivers()
        {
            // Two-pass to avoid mutation during foreach.
            List<CarId> stillRacing = null;
            foreach (var kv in _drivers)
            {
                if (kv.Value == DriverRaceState.Racing)
                {
                    stillRacing ??= new List<CarId>();
                    stillRacing.Add(kv.Key);
                }
            }
            if (stillRacing == null) return;
            foreach (var id in stillRacing)
            {
                _drivers[id] = DriverRaceState.Eliminated;
                _eliminatedCount++;
                Publish(new DriverEliminatedEvent(id, EpisodeEndReason.Failure_Timeout));
            }
        }

        private void EndRace(RaceEndReason reason)
        {
            _state = RaceLifecycleState.Ended;
            foreach (var k in _drivers.Keys) _pendingEnd.Add(k);
            Publish(new RaceEndedEvent(_currentRaceId, _finishersCount, _eliminatedCount, reason));
        }

        private void AbortRace()
        {
            if (_state != RaceLifecycleState.Active) return;
            EndRace(RaceEndReason.Aborted);
        }
    }

    /// <summary>
    /// Installs <see cref="IRaceCoordinator"/> and binds its env-var hooks.
    /// The supervisor sets RACING_RACE_SCOPED / RACING_LAP_TARGET to pin
    /// behaviour without touching the yaml; absent env vars default to
    /// race-scoped with lap target 3 (the canonical Latest training shape).
    /// </summary>
    public sealed class RaceCoordinatorSystemInstaller : ISystemInstaller
    {
        public void Install(ContainerBuilder builder)
        {
            builder.AddSingleton(c => new RaceCoordinator(
                    c.Resolve<IEventBus>(),
                    TryResolve<ITimeProvider>(c),
                    envOverride: ReadRaceScopedEnv,
                    lapTargetOverride: ReadLapTargetEnv),
                typeof(IRaceCoordinator), typeof(RaceCoordinator));
        }

        public ISystemTestFactory CreateTestFactory() => new RaceCoordinatorTestFactory();

        // Local optional-resolve helper. Mirrors AiDriverContainerExtensions
        // but avoids a cross-namespace dependency from the Race namespace
        // back into AiDriver root (which would pull AiDriverContainerExtensions
        // into this Race-namespace installer's scope).
        private static T TryResolve<T>(Container c) where T : class
        {
            try { return c.Resolve<T>(); }
            catch { return null; }
        }

        // float.NaN sentinel = "not set" so the coordinator falls through to
        // its default (race-scoped). Any 0/1 in the env wins outright.
        private static float ReadRaceScopedEnv()
        {
            string raw = System.Environment.GetEnvironmentVariable("RACING_RACE_SCOPED");
            if (string.IsNullOrEmpty(raw)) return float.NaN;
            if (float.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v)) return v;
            return float.NaN;
        }

        private static float ReadLapTargetEnv()
        {
            string raw = System.Environment.GetEnvironmentVariable("RACING_LAP_TARGET");
            if (string.IsNullOrEmpty(raw)) return float.NaN;
            if (float.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v)) return v;
            return float.NaN;
        }
    }

    internal sealed class RaceCoordinatorTestFactory : ISystemTestFactory
    {
        public Type[] TestedServices => new[] { typeof(IRaceCoordinator) };
        public object CreateForTesting(TestDependencies deps) => null;
        public IEnumerable<ITestScenario> GetScenarios()
        {
            yield return new RaceCoordinatorLifecycleScenario();
        }
    }

    /// <summary>
    /// Smoke scenario: drives a fake 3-driver race through the coordinator
    /// — two finishers + one elimination — and confirms exactly one
    /// RaceStartedEvent + one RaceEndedEvent fire, with the correct counts.
    /// </summary>
    internal sealed class RaceCoordinatorLifecycleScenario : DataDrivenScenario
    {
        private ScenarioEventBus _bus;
        private RaceCoordinator _coordinator;
        private int _raceStarted;
        private int _raceEnded;
        private RaceEndedEvent _lastEndEvent;

        public RaceCoordinatorLifecycleScenario() : base(new TestScenarioDefinition(
            "ai-driver-race-coordinator-lifecycle",
            "AI Driver — Race Coordinator (3-Driver Lifecycle)",
            "Spawns 3 drivers, finishes 2 + eliminates 1, asserts a single race-end fires with finishers=2 eliminated=1.",
            Array.Empty<ScenarioParameter>()))
        { }

        protected override void ExecuteInternal(ScenarioParameterOverrides overrides)
        {
            _bus = new ScenarioEventBus();
            _bus.Subscribe<RaceStartedEvent>(_ => _raceStarted++);
            _bus.Subscribe<RaceEndedEvent>(e => { _raceEnded++; _lastEndEvent = e; });

            // Force race-scoped on via env override.
            var coord = new RaceCoordinator(_bus, time: null,
                envOverride: () => 1f, lapTargetOverride: () => 3f);
            _coordinator = coord;

            var c1 = new CarId(101);
            var c2 = new CarId(102);
            var c3 = new CarId(103);
            _bus.Publish(new CarSpawnedEvent(c1, Vector3.zero, 0f));
            _bus.Publish(new CarSpawnedEvent(c2, Vector3.zero, 0f));
            _bus.Publish(new CarSpawnedEvent(c3, Vector3.zero, 0f));

            _bus.Publish(new DriverFinishedRaceEvent(c1, 1, 90f, 3));
            _bus.Publish(new DriverEliminatedEvent(c2, EpisodeEndReason.Failure_Wreck));
            _bus.Publish(new DriverFinishedRaceEvent(c3, 2, 95f, 3));

            Debug.Log($"[RaceCoordinatorScenario] started={_raceStarted} ended={_raceEnded} " +
                      $"finishers={_lastEndEvent.FinishersCount} eliminated={_lastEndEvent.EliminatedCount} " +
                      $"reason={_lastEndEvent.Reason}");
        }

        protected override ScenarioVerificationResult VerifyInternal(ScenarioParameterOverrides overrides)
        {
            var checks = new List<ScenarioVerificationResult.CheckResult>
            {
                new("coordinator constructed", _coordinator != null, "coordinator null"),
                new("exactly one RaceStartedEvent", _raceStarted == 1, $"got {_raceStarted}"),
                new("exactly one RaceEndedEvent", _raceEnded == 1, $"got {_raceEnded}"),
                new("end reason AllDriversResolved",
                    _lastEndEvent.Reason == RaceEndReason.AllDriversResolved,
                    $"got {_lastEndEvent.Reason}"),
                new("finishers = 2", _lastEndEvent.FinishersCount == 2,
                    $"got {_lastEndEvent.FinishersCount}"),
                new("eliminated = 1", _lastEndEvent.EliminatedCount == 1,
                    $"got {_lastEndEvent.EliminatedCount}"),
            };
            return new ScenarioVerificationResult(checks);
        }

        protected override void OnCleanup()
        {
            _coordinator?.Dispose();
            _coordinator = null;
            _bus = null;
        }
    }
}
