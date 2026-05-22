using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.Terrain.Showcase;
using Unidad.Core.EventBus;
using Unidad.Core.Testing;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Terrain.Scenarios
{
    /// <summary>
    /// Wires the showcase service end-to-end (terrain + mesh + camera orbit).
    /// Runs in editor; verifies deterministic setup. Camera animation runs from
    /// the live scene tick — the scenario only checks the rig is in place.
    /// </summary>
    internal sealed class TerrainShowcaseScenario : DataDrivenScenario
    {
        private IEventBus _eventBus;
        private TerrainService _service;
        private TerrainMeshBuilder _builder;
        private ScenarioGameObjectFactory _factory;
        private TerrainShowcaseService _showcase;
        private readonly List<IDisposable> _subs = new();

        // Geometry must mirror the const block in TerrainShowcaseService —
        // the service initializes a fixed 48x48 grid (corner field = 49x49).
        private const int ExpectedWidth = 48;
        private const int ExpectedDepth = 48;
        private const int ExpectedCornerWidth = 49;
        private const int ExpectedCornerDepth = 49;

        public TerrainShowcaseScenario() : base(new TestScenarioDefinition(
            "terrain-showcase",
            "Terrain Showcase (Live)",
            "Boots a 48x48 random terrain via TerrainShowcaseService and orbits the camera around it.",
            Array.Empty<ScenarioParameter>()))
        { }

        protected override void ExecuteInternal(ScenarioParameterOverrides overrides)
        {
            _eventBus = new ScenarioEventBus();
            _service = new TerrainService(_eventBus);
            _builder = new TerrainMeshBuilder();
            _factory = new ScenarioGameObjectFactory();
            _showcase = new TerrainShowcaseService(_eventBus, _service, _builder, _factory);

            _subs.Add(_eventBus.Subscribe<TerrainInitializedEvent>(e =>
                Debug.Log($"[TerrainShowcaseScenario] Initialized {e.Width}x{e.Depth}")));

            _showcase.Start();
            Debug.Log("[TerrainShowcaseScenario] Showcase running");
        }

        protected override ScenarioVerificationResult VerifyInternal(ScenarioParameterOverrides overrides)
        {
            var checks = new List<ScenarioVerificationResult.CheckResult>
            {
                new("Showcase running", _showcase != null && _showcase.IsRunning,
                    _showcase?.IsRunning == true ? null : "Showcase not running"),
                new("Terrain initialized",
                    _service != null && _service.IsInitialized
                                    && _service.Width == ExpectedWidth
                                    && _service.Depth == ExpectedDepth,
                    _service == null
                        ? "TerrainService not constructed"
                        : $"expected {ExpectedWidth}x{ExpectedDepth}, got {_service.Width}x{_service.Depth}"),
                new("Corner field present",
                    _service != null
                        && _service.CornerWidth == ExpectedCornerWidth
                        && _service.CornerDepth == ExpectedCornerDepth,
                    _service == null
                        ? "TerrainService not constructed"
                        : $"expected corner {ExpectedCornerWidth}x{ExpectedCornerDepth}, got {_service.CornerWidth}x{_service.CornerDepth}"),
            };
            return new ScenarioVerificationResult(checks);
        }

        protected override void OnCleanup()
        {
            _showcase?.Stop();
            foreach (var s in _subs) s.Dispose();
            _subs.Clear();
            _factory?.Dispose();
            _eventBus?.ClearAllSubscriptions();
            _eventBus = null;
            _service = null;
            _builder = null;
            _factory = null;
            _showcase = null;
        }
    }
}
