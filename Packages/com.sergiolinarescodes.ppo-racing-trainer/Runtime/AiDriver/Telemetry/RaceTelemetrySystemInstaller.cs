using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.AiDriver;
using UnityPpoRacingTrainer.Core.AiDriver.Physics;
using UnityPpoRacingTrainer.Core.AiDriver.Physics.Draft;
using UnityPpoRacingTrainer.Core.AiDriver.Physics.Fuel;
using UnityPpoRacingTrainer.Core.AiDriver.Physics.Tires;
using UnityPpoRacingTrainer.Core.AiDriver.Policy;
using UnityPpoRacingTrainer.Core.AiDriver.Race;
using UnityPpoRacingTrainer.Core.AiDriver.Training;
using UnityPpoRacingTrainer.Core.Terrain.Scenarios;
using UnityPpoRacingTrainer.Core.Track;
using Reflex.Core;
using Unidad.Core.Bootstrap;
using Unidad.Core.EventBus;
using Unidad.Core.Testing;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.AiDriver.Telemetry
{
    /// <summary>
    /// Registers the race-telemetry recorder, the reservoir sampler, and a
    /// sink/store pair (disk or in-memory). Construct with
    /// <c>useDiskSink: true</c> from the headless trainer so kept races
    /// land under <c>results/_telemetry/races/</c>; pass <c>false</c> from
    /// the player game so the post-race UI can read the same DTO graph
    /// from an in-process ring buffer.
    /// </summary>
    public sealed class RaceTelemetrySystemInstaller : ISystemInstaller
    {
        public const int DefaultWindowSize = 1000;

        private readonly bool _useDiskSink;
        private readonly int _windowSize;
        private readonly int _maxKept;
        private readonly int _expectedDriversPerRound;

        public RaceTelemetrySystemInstaller(
            bool useDiskSink,
            int windowSize = DefaultWindowSize,
            int maxKept = DiskJsonRaceSink.DefaultMaxKept,
            int expectedDriversPerRound = RaceTelemetryService.DefaultExpectedDriversPerRound)
        {
            _useDiskSink = useDiskSink;
            _windowSize = windowSize <= 0 ? DefaultWindowSize : windowSize;
            _maxKept = maxKept <= 0 ? DiskJsonRaceSink.DefaultMaxKept : maxKept;
            _expectedDriversPerRound = expectedDriversPerRound > 0
                ? expectedDriversPerRound
                : RaceTelemetryService.DefaultExpectedDriversPerRound;
        }

        public void Install(ContainerBuilder builder)
        {
            // Sampler — pure CPU, no event subs, registered as singleton so
            // the window count survives across episode boundaries.
            builder.AddSingleton(_ => new ReservoirRaceSampler(_windowSize),
                typeof(ReservoirRaceSampler));

            // Sink + store are the same object — both interfaces, one
            // implementation. Switching between disk/memory is purely a
            // bootstrap-time concern; consumers see the abstraction only.
            if (_useDiskSink)
            {
                // 10-min min-age floor (DiskJsonRaceSink.DefaultMinAgeBeforePruneSeconds)
                // shields recently-written races from prune so the
                // dashboard race-detail view stays valid for at least that
                // long. Pin sidecar (`.pinned`) extends protection per-race
                // for any race the dashboard is currently displaying.
                builder.AddSingleton(_ => new DiskJsonRaceSink(_maxKept),
                    typeof(IRaceTelemetrySink), typeof(IRaceHistoryStore));
            }
            else
            {
                builder.AddSingleton(_ => new InMemoryRaceSink(_maxKept),
                    typeof(IRaceTelemetrySink), typeof(IRaceHistoryStore));
            }

            builder.AddSingleton(c => new RaceTelemetryService(
                    c.Resolve<IEventBus>(),
                    c.Resolve<IRaceTelemetrySink>(),
                    c.Resolve<ReservoirRaceSampler>(),
                    c.TryResolveOptional<ITirePhysicsService>(),
                    c.TryResolveOptional<IFuelService>(),
                    c.TryResolveOptional<IDraftService>(),
                    c.TryResolveOptional<IRaceStateService>(),
                    clock: null,
                    expectedDriversPerRound: _expectedDriversPerRound,
                    coord: c.TryResolveOptional<IRaceCoordinator>()),
                typeof(IRaceTelemetryRecorder));
        }

        public ISystemTestFactory CreateTestFactory() => new RaceTelemetryTestFactory();
    }

    internal sealed class RaceTelemetryTestFactory : ISystemTestFactory
    {
        public Type[] TestedServices => new[]
        {
            typeof(IRaceTelemetryRecorder),
            typeof(IRaceTelemetrySink),
            typeof(IRaceHistoryStore),
        };

        public object CreateForTesting(TestDependencies deps) => null;

        public IEnumerable<ITestScenario> GetScenarios()
        {
            yield return new RaceTelemetryReservoirScenario();
        }
    }

    /// <summary>
    /// Visible smoke for the recorder + sampler + in-memory sink chain.
    /// Builds a synthetic 2-car race through a <see cref="ScenarioEventBus"/>
    /// using a 1-deep reservoir (window of 1) so the first finished race
    /// is forcibly flushed to the in-memory store. Verify() then asserts
    /// the deterministic post-state — the sink holds exactly one race
    /// with the expected drivers and event types.
    /// </summary>
    internal sealed class RaceTelemetryReservoirScenario : DataDrivenScenario
    {
        private ScenarioEventBus _bus;
        private InMemoryRaceSink _sink;
        private ReservoirRaceSampler _sampler;
        private RaceTelemetryService _service;

        public RaceTelemetryReservoirScenario() : base(new TestScenarioDefinition(
            "ai-driver-race-telemetry-reservoir",
            "AI Driver — Race Telemetry (Reservoir, In-Memory)",
            "Drives a synthetic 2-car race through the recorder, forces a window-close on episode end, and confirms one race lands in the in-memory store with the expected event types.",
            Array.Empty<ScenarioParameter>()))
        { }

        protected override void ExecuteInternal(ScenarioParameterOverrides overrides)
        {
            _bus = new ScenarioEventBus();
            _sink = new InMemoryRaceSink(maxKept: 50);
            _sampler = new ReservoirRaceSampler(windowSize: 1);
            // 2 cars in this scenario — counter gate flushes once both end.
            _service = new RaceTelemetryService(_bus, _sink, _sampler, expectedDriversPerRound: 2);

            var car1 = new CarId(1);
            var car2 = new CarId(2);
            _bus.Publish(new CarSpawnedEvent(car1, Vector3.zero, 0f));
            _bus.Publish(new CarSpawnedEvent(car2, new Vector3(2f, 0f, 0f), 0f));

            // 3 seconds of synthetic 50 Hz tick stream → ~3 emitted 1 Hz samples per car.
            const float dt = 1f / 50f;
            for (int i = 0; i < 50 * 3; i++)
            {
                _bus.Publish(new CarPhysicsTickedEvent(car1, new Vector3(i * 0.1f, 0f, 0f), 0f, 25f,
                    LateralAcceleration: 0f, Slip: 0.0f, ThrottleInput: 0.5f, BrakeInput: 0f,
                    Surface: SurfaceKind.Asphalt, ArcLengthAlong: i * 0.5f, Dt: dt));
                _bus.Publish(new CarPhysicsTickedEvent(car2, new Vector3(i * 0.1f, 0f, 2f), 0f, 23f,
                    LateralAcceleration: 0f, Slip: 0.0f, ThrottleInput: 0.5f, BrakeInput: 0f,
                    Surface: SurfaceKind.Asphalt, ArcLengthAlong: i * 0.5f, Dt: dt));
            }

            _bus.Publish(new CarLapCompletedEvent(car1, 1, 32.5f));
            _bus.Publish(new OvertakeEvent(car1, car2, 1));
            _bus.Publish(new TirePuncturedEvent(car2, TireSide.Right, 0.99f));

            _bus.Publish(new EpisodeEndedEvent(car1, EpisodeEndReason.Success, 142.7f, 150, 3.0f, 1));
            _bus.Publish(new EpisodeEndedEvent(car2, EpisodeEndReason.Failure_PuncturedAndOffTrack, -8.4f, 150, 3.0f, 0));

            var stored = _sink.List();
            Debug.Log($"[RaceTelemetryScenario] stored races = {stored.Count}");
            if (stored.Count > 0)
            {
                var full = _sink.Load(stored[0].race_id);
                Debug.Log($"[RaceTelemetryScenario] drivers = {full.drivers.Count} events = {full.events.Count} duration = {full.duration_s:F2}s");
                foreach (var d in full.drivers)
                {
                    Debug.Log($"[RaceTelemetryScenario] car {d.car_id} reason={d.end_state.reason} laps={d.end_state.laps_completed} samples={d.samples.Count} reward={d.end_state.cumulative_reward:F2}");
                }
            }
        }

        protected override ScenarioVerificationResult VerifyInternal(ScenarioParameterOverrides overrides)
        {
            var checks = new List<ScenarioVerificationResult.CheckResult>();
            checks.Add(new("recorder service constructed", _service != null, "RaceTelemetryService null"));
            checks.Add(new("sink holds one race", _sink != null && _sink.List().Count == 1,
                $"expected 1 race, got {(_sink == null ? -1 : _sink.List().Count)}"));
            if (_sink != null && _sink.List().Count == 1)
            {
                var full = _sink.Load(_sink.List()[0].race_id);
                checks.Add(new("race has 2 drivers", full != null && full.drivers != null && full.drivers.Count == 2,
                    "driver count mismatch"));
                checks.Add(new("race has event entries", full != null && full.events != null && full.events.Count >= 3,
                    "event count below expected"));
            }
            return new ScenarioVerificationResult(checks);
        }

        protected override void OnCleanup()
        {
            _service?.Dispose();
            _service = null;
            _sink = null;
            _sampler = null;
            _bus = null;
        }
    }
}
