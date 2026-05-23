using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.Terrain;
using UnityPpoRacingTrainer.Core.Terrain.Scenarios;
using UnityPpoRacingTrainer.Core.Track.Presentation;
using UnityPpoRacingTrainer.Core.Track.Shape;
using Reflex.Core;
using Unidad.Core.Bootstrap;
using Unidad.Core.EventBus;
using Unidad.Core.Factory;
using Unidad.Core.Grid;
using Unidad.Core.Testing;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Track
{
    /// <summary>
    /// Wraps the magnet snap math behind a stateful service. Owns the
    /// <see cref="OpenPortIndex"/> lifecycle and rebuilds it lazily — once after any
    /// piece is placed or removed, then reused for every cursor query until the next
    /// change. Holds a one-bit "engaged" flag so the caller can use a wider release
    /// radius than the engage radius (hysteresis) — without it, micro-jitter at the
    /// boundary pops the snap on/off every frame.
    /// </summary>
    internal sealed class SnapResolverService : IDisposable
    {
        private readonly ITrackPlacementService _placement;
        private readonly ITrackPieceCatalog _catalog;
        private readonly ITerrainService _terrain;
        private readonly OpenPortIndex _index = new();
        private readonly IDisposable _placedSub;
        private readonly IDisposable _removedSub;
        private readonly System.Collections.Generic.List<VariantOption> _variants = new();
        private bool _dirty = true;
        private bool _engaged;

        private TrackShape _cacheSrc;
        private TrackShape _cacheMirror;
        private TrackShape _cacheDiagR;
        private TrackShape _cacheDiagL;
        private TrackShape _cacheDiagRMirror;
        private TrackShape _cacheDiagLMirror;
        private bool _variantsValid;
        private bool _variantsIncludeDiag;

        public SnapResolverService(IEventBus eventBus, ITrackPlacementService placement, ITrackPieceCatalog catalog, ITerrainService terrain = null)
        {
            _placement = placement;
            _catalog = catalog;
            _terrain = terrain;
            _placedSub = eventBus.Subscribe<TrackPiecePlacedEvent>(_ => MarkDirty());
            _removedSub = eventBus.Subscribe<TrackPieceRemovedEvent>(_ => MarkDirty());
        }

        /// <summary>
        /// True when a magnet snap is active for this frame: the cursor is within the
        /// engage radius (or within the wider release radius if already engaged), AND
        /// the active shape's anchor port can mate with the nearest open port. Out-
        /// params describe the resolved origin, the auto-aligned facing that mates the
        /// ports, and the matched <see cref="OpenPort"/>. All defaulted on miss.
        /// </summary>
        public bool TryResolve(
            TrackShape shape,
            Vector3 cursorWorld,
            float snapRadius,
            float releaseRadius,
            int anchorCycleIndex,
            out TrackShape resolvedShape,
            out GridPosition origin,
            out TrackDirection alignedFacing,
            out OpenPort target,
            out int variantCount)
        {
            origin = default;
            alignedFacing = TrackDirection.North;
            target = default;
            variantCount = 0;
            resolvedShape = shape;
            EnsureFresh();

            float radius = _engaged ? releaseRadius : snapRadius;
            if (!_index.TryFindNearest(cursorWorld, radius, out var open))
            {
                _engaged = false;
                return false;
            }

            // The variant set is a flat list of (shape, anchor piece, anchor port)
            // candidates. Cardinal-target variants: original shape + X-mirror.
            // Diagonal-target variants: those plus right/left diagonal-entry wraps
            // (each prepends a CurveDiagTransition so the wrapped shape's first
            // boundary port is a diagonal corner). Same placement pipeline handles
            // both — the magnet always solves a cardinal shape facing, integer-grid
            // offsets stay correct, no special-case rendering. Diagonal "support"
            // is purely data: extra TrackShape variants composed from the same piece
            // catalog the walker already uses.
            BuildVariants(shape, includeDiagonalWraps: !open.OutwardDirection.IsCardinal());
            variantCount = _variants.Count;
            if (variantCount == 0)
            {
                _engaged = false;
                return false;
            }

            for (int step = 0; step < variantCount; step++)
            {
                int idx = ((((anchorCycleIndex + step) % variantCount) + variantCount) % variantCount);
                var v = _variants[idx];

                if (!MagnetSnapResolver.TryResolveAlignedAt(
                        v.Shape, v.PieceIdx, v.PortIdx, open, _catalog,
                        CellSize, out origin, out alignedFacing)) continue;
                if (!alignedFacing.IsCardinal()) continue;

                target = open;
                resolvedShape = v.Shape;
                _engaged = true;
                return true;
            }

            _engaged = false;
            return false;
        }

        private readonly struct VariantOption
        {
            public readonly TrackShape Shape;
            public readonly int PieceIdx;
            public readonly int PortIdx;
            public VariantOption(TrackShape s, int p, int o) { Shape = s; PieceIdx = p; PortIdx = o; }
        }

        private void BuildVariants(TrackShape shape, bool includeDiagonalWraps)
        {
            bool cacheChanged = !ReferenceEquals(_cacheSrc, shape);
            if (cacheChanged) RebuildCache(shape);

            // Skip the per-frame boundary re-enumeration when nothing relevant
            // changed — ShapeBoundaryPorts.Enumerate allocates List+Dictionary per
            // call, so the saved frames matter on a per-Tick magnet path.
            if (!cacheChanged && _variantsValid && _variantsIncludeDiag == includeDiagonalWraps)
                return;

            _variants.Clear();
            AppendBoundary(shape);
            AppendBoundary(_cacheMirror);
            if (includeDiagonalWraps)
            {
                AppendBoundary(_cacheDiagR);
                AppendBoundary(_cacheDiagL);
                AppendBoundary(_cacheDiagRMirror);
                AppendBoundary(_cacheDiagLMirror);
            }
            _variantsIncludeDiag = includeDiagonalWraps;
            _variantsValid = true;
        }

        private void AppendBoundary(TrackShape variant)
        {
            if (variant == null) return;
            var ports = ShapeBoundaryPorts.Enumerate(variant, _catalog);
            for (int i = 0; i < ports.Count; i++)
                _variants.Add(new VariantOption(variant, ports[i].PieceIndex, ports[i].PortIndex));
        }

        private void RebuildCache(TrackShape shape)
        {
            _cacheSrc = shape;
            _cacheMirror = TrackShapeMirror.Mirror(shape);
            _cacheDiagR = WrapOrNull(shape, rightTransition: true);
            _cacheDiagL = WrapOrNull(shape, rightTransition: false);
            _cacheDiagRMirror = WrapOrNull(_cacheMirror, rightTransition: true);
            _cacheDiagLMirror = WrapOrNull(_cacheMirror, rightTransition: false);
            _variantsValid = false;
        }

        private static TrackShape WrapOrNull(TrackShape src, bool rightTransition)
            => src != null ? TrackShapeDiagonalWrap.Wrap(src, rightTransition) : null;

        private void MarkDirty()
        {
            _dirty = true;
            _engaged = false; // placed/removed pieces invalidate the engaged port
        }

        private float CellSize => _terrain != null && _terrain.IsInitialized
            ? _terrain.CellSize
            : 1f;

        private void EnsureFresh()
        {
            if (!_dirty) return;
            _index.Rebuild(_placement.Placed, _catalog, CellSize);
            _dirty = false;
        }

        public void Dispose()
        {
            _placedSub?.Dispose();
            _removedSub?.Dispose();
            _engaged = false;
        }
    }

    // -------------------------------------------------------------------------
    // Shape Placement Pipeline — shared per-frame placement core used by every
    // mouse-driven build site (main-scene debug authoring, MouseShapePlacementScenario,
    // TrackPartsEditorScenario). Co-located in this file rather than a new file so
    // dotnet build stays clean before Unity regenerates csproj (per project memory:
    // "New .cs files break dotnet build until Unity refreshes — co-locate when possible").
    // Owns: snap resolver, placement state machine, ghost preview renderer.
    // Does NOT own: input policy, undo, drag-build chain, shape catalogs.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Drive a session by:
    /// <c>Begin(cfg)</c> → <c>SetShape</c> → repeated <c>Tick(origin, worldHit, ...)</c> →
    /// <c>Commit()</c> on LMB, <c>Cancel()</c> on Esc, <c>End(session)</c> on teardown.
    /// </summary>
    public interface IShapePlacementPipeline
    {
        PipelineSession Begin(PipelineConfig cfg);
        void End(PipelineSession s);

        void SetShape(PipelineSession s, TrackShape shape, TrackPieceVariantId variantId);
        void SetFacing(PipelineSession s, TrackDirection facing);
        /// <summary>R-key-while-magnet path — advances the snap anchor variant.</summary>
        void CycleAnchor(PipelineSession s, int delta);
        void Invalidate(PipelineSession s);

        /// <summary>Returns the resolved frame: snapped vs free, valid/magnet flags,
        /// piece-level preview the ghost renderer painted this frame. Off-grid
        /// callers pass <paramref name="cursorOnGrid"/>=false.</summary>
        PipelineFrame Tick(PipelineSession s, GridPosition origin, Vector3 worldHit, bool cursorOnGrid, float deltaTime);

        /// <summary>Commit the last Tick's frame via IShapePlacementService.
        /// Honors <see cref="PipelineConfig.Animate"/>. Returns the placement result.</summary>
        ShapePlacementResult Commit(PipelineSession s);

        /// <summary>Hide ghost, reset state machine. Shape/facing/variant kept.</summary>
        void Cancel(PipelineSession s);
    }

    /// <summary>
    /// Per-session config. <see cref="GhostRoot"/> owns the per-session ghost mesh pool
    /// so multiple call sites can run concurrent sessions without their ghosts colliding.
    /// <see cref="Animate"/> defaults to true — placement service drop-from-air animation
    /// runs on each commit; set to false for procedural / bulk-load paths.
    /// </summary>
    public readonly record struct PipelineConfig(
        GameObject GhostRoot,
        float MagnetSnapRadius,
        float MagnetReleaseRadius,
        Color ValidColor,
        Color InvalidColor,
        Color MagnetColor,
        ITerrainService Terrain,
        bool Animate = true);

    /// <summary>
    /// Output of one Tick. <see cref="HasPreview"/>=false when the cursor is off-grid
    /// (no shape rendered). <see cref="Shape"/> reflects any magnet-induced variant
    /// (mirror / diagonal wrap), so callers commit the resolved shape, not the
    /// originally-set one.
    /// </summary>
    public readonly record struct PipelineFrame(
        bool HasPreview,
        GridPosition Origin,
        TrackDirection Facing,
        TrackShape Shape,
        TrackPieceVariantId VariantId,
        bool MagnetActive,
        bool AllValid,
        ShapePreviewResult Preview);

    /// <summary>
    /// Opaque per-session handle. Holds the per-call-site snap resolver +
    /// placement state machine + ghost pool. Owned by the pipeline; disposed
    /// via <see cref="IShapePlacementPipeline.End"/>.
    /// </summary>
    public sealed class PipelineSession
    {
        internal readonly SnapResolverService Snap;
        internal readonly PlacementStateMachine State;
        internal readonly GhostPreviewRenderer Ghost;
        internal readonly PipelineConfig Cfg;
        internal TrackShape Shape;
        internal TrackPieceVariantId VariantId = TrackPieceVariantId.Default;
        internal TrackDirection Facing = TrackDirection.North;
        internal int AnchorCycleIndex;
        internal bool LastMagnet;
        internal PipelineFrame Last;

        internal PipelineSession(SnapResolverService snap, PlacementStateMachine state, GhostPreviewRenderer ghost, PipelineConfig cfg)
        {
            Snap = snap;
            State = state;
            Ghost = ghost;
            Cfg = cfg;
        }
    }

    internal sealed class ShapePlacementPipeline : IShapePlacementPipeline
    {
        private readonly IEventBus _bus;
        private readonly IShapePreviewService _preview;
        private readonly IShapePlacementService _shapePlacement;
        private readonly ITrackPlacementService _trackPlacement;
        private readonly ITrackPieceCatalog _catalog;
        private readonly ITrackPieceMeshBuilder _meshBuilder;
        private readonly IGameObjectFactory _factory;

        public ShapePlacementPipeline(
            IEventBus bus,
            IShapePreviewService preview,
            IShapePlacementService shapePlacement,
            ITrackPlacementService trackPlacement,
            ITrackPieceCatalog catalog,
            ITrackPieceMeshBuilder meshBuilder,
            IGameObjectFactory factory)
        {
            _bus = bus;
            _preview = preview;
            _shapePlacement = shapePlacement;
            _trackPlacement = trackPlacement;
            _catalog = catalog;
            _meshBuilder = meshBuilder;
            _factory = factory;
        }

        public PipelineSession Begin(PipelineConfig cfg)
        {
            if (cfg.GhostRoot == null)
                throw new ArgumentException("PipelineConfig.GhostRoot must be non-null", nameof(cfg));

            var snap = new SnapResolverService(_bus, _trackPlacement, _catalog, cfg.Terrain);
            var state = new PlacementStateMachine();
            var ghost = new GhostPreviewRenderer(
                _catalog, _meshBuilder, _factory, cfg.GhostRoot,
                cfg.ValidColor, cfg.InvalidColor, cfg.MagnetColor);
            return new PipelineSession(snap, state, ghost, cfg);
        }

        public void End(PipelineSession s)
        {
            if (s == null) return;
            s.Ghost.HideAll();
            s.Ghost.Dispose();
            s.Snap.Dispose();
            s.Last = default;
        }

        public void SetShape(PipelineSession s, TrackShape shape, TrackPieceVariantId variantId)
        {
            if (s == null) return;
            s.Shape = shape;
            s.VariantId = variantId;
            s.AnchorCycleIndex = 0;
            // Mid-session shape change must reset magnet hysteresis — otherwise the
            // new shape inherits the engaged latch from the previous shape's variant
            // set and snaps to a stale port the first frame.
            s.State.Invalidate();
        }

        public void SetFacing(PipelineSession s, TrackDirection facing)
        {
            if (s == null) return;
            s.Facing = facing;
            s.State.Invalidate();
        }

        public void CycleAnchor(PipelineSession s, int delta)
        {
            if (s == null) return;
            s.AnchorCycleIndex += delta;
            s.State.Invalidate();
        }

        public void Invalidate(PipelineSession s) => s?.State.Invalidate();

        public PipelineFrame Tick(PipelineSession s, GridPosition origin, Vector3 worldHit, bool cursorOnGrid, float deltaTime)
        {
            if (s == null || s.Shape == null) return default;

            if (!cursorOnGrid)
            {
                if (s.State.Update(default, 0, s.Facing, true, false))
                    s.Ghost.HideAll();
                s.LastMagnet = false;
                s.Last = default;
                return s.Last;
            }

            bool magnetActive = s.Snap.TryResolve(
                s.Shape, worldHit, s.Cfg.MagnetSnapRadius, s.Cfg.MagnetReleaseRadius,
                s.AnchorCycleIndex,
                out var resolvedShape, out var snappedOrigin, out var alignedFacing,
                out _, out _);

            var renderShape = magnetActive ? resolvedShape : s.Shape;
            var renderOrigin = magnetActive ? snappedOrigin : origin;
            var renderFacing = magnetActive ? alignedFacing : s.Facing;

            bool dirty = s.State.Update(renderOrigin, 0, renderFacing, false, magnetActive);
            ShapePreviewResult preview = dirty || !s.Last.HasPreview
                ? _preview.Compute(renderShape, renderOrigin, renderFacing)
                : s.Last.Preview;

            bool justEngaged = magnetActive && !s.LastMagnet;
            s.Ghost.Render(preview, s.Cfg.Terrain, deltaTime, magnetActive, s.VariantId, justEngaged);
            s.LastMagnet = magnetActive;

            s.Last = new PipelineFrame(
                HasPreview: true,
                Origin: renderOrigin,
                Facing: renderFacing,
                Shape: renderShape,
                VariantId: s.VariantId,
                MagnetActive: magnetActive,
                AllValid: preview.AllValid,
                Preview: preview);
            return s.Last;
        }

        public ShapePlacementResult Commit(PipelineSession s)
        {
            if (s == null || s.Shape == null || !s.Last.HasPreview || !s.Last.AllValid)
                return new ShapePlacementResult(false, Array.Empty<TrackPieceId>(), 0, 0);

            var res = _shapePlacement.TryPlaceShape(
                s.Last.Shape, s.Last.Origin, s.Last.Facing, s.VariantId, s.Cfg.Animate);
            // Occupancy + open-port set just changed; force next Tick to re-resolve
            // snap / re-compute preview even if the cursor stays on the same cell.
            s.State.Invalidate();
            return res;
        }

        public void Cancel(PipelineSession s)
        {
            if (s == null) return;
            s.Ghost.HideAll();
            s.State.Invalidate();
            s.LastMagnet = false;
            s.Last = default;
        }
    }

    public sealed class ShapePlacementPipelineSystemInstaller : ISystemInstaller
    {
        public void Install(ContainerBuilder builder)
        {
            builder.AddSingleton(c => new ShapePlacementPipeline(
                    c.Resolve<IEventBus>(),
                    c.Resolve<IShapePreviewService>(),
                    c.Resolve<IShapePlacementService>(),
                    c.Resolve<ITrackPlacementService>(),
                    c.Resolve<ITrackPieceCatalog>(),
                    c.Resolve<ITrackPieceMeshBuilder>(),
                    c.Resolve<IGameObjectFactory>()),
                typeof(IShapePlacementPipeline));
        }

        public ISystemTestFactory CreateTestFactory() => new ShapePlacementPipelineTestFactory();
    }

    internal sealed class ShapePlacementPipelineTestFactory : ISystemTestFactory
    {
        public Type[] TestedServices => new[] { typeof(IShapePlacementPipeline) };
        public object CreateForTesting(TestDependencies deps) => null;
        public IEnumerable<ITestScenario> GetScenarios()
        {
            yield return new ShapePlacementPipelineSmokeScenario();
        }
    }

    /// <summary>
    /// Visual parity scenario. Constructs two independent pipeline instances and
    /// drives them with the same scripted trajectory, then renders the placed
    /// pieces side-by-side under the SceneRoot. Console logs the PieceIds from
    /// both sides — if the lists diverge the caller paths have drifted.
    /// Used as a smoke gate when refactoring the shared placement core; the
    /// load-bearing NUnit equivalent lives at
    /// <c>ShapePlacementPipelineParityTests</c>.
    /// </summary>
    internal sealed class ShapePlacementPipelineSmokeScenario : DataDrivenScenario
    {
        private bool _ok;
        private string _diag;

        public ShapePlacementPipelineSmokeScenario() : base(new TestScenarioDefinition(
            "shape-placement-pipeline-parity",
            "Shape Placement Pipeline — Visual Parity",
            "Builds two independent pipeline instances on identical service graphs, drives both with the same scripted (SetShape → SetFacing → Tick → Commit) trajectory, asserts identical results, and renders both placements under SceneRoot. Console logs PieceIds — divergence = a regression in the shared building core. Verify() checks only deterministic setup (per scenario rules).",
            Array.Empty<ScenarioParameter>()))
        { }

        protected override void ExecuteInternal(ScenarioParameterOverrides overrides)
        {
            _ok = false;
            _diag = "init";
            try
            {
                var lhs = BuildHarness("LHS");
                var rhs = BuildHarness("RHS");

                var shape = PickShape(lhs.Shapes);
                if (shape == null)
                {
                    _diag = "Seeded TrackShapeCatalog empty";
                    return;
                }

                var rL = DriveOnce(lhs, shape, new GridPosition(2, 2), TrackDirection.East);
                var rR = DriveOnce(rhs, shape, new GridPosition(8, 2), TrackDirection.East);

                UnityEngine.Debug.Log(
                    $"[PipelinePartiy] LHS success={rL.Success} pieces={rL.PieceIds.Count} | " +
                    $"RHS success={rR.Success} pieces={rR.PieceIds.Count}");

                _ok = rL.Success == rR.Success
                      && rL.PieceIds.Count == rR.PieceIds.Count
                      && rL.TotalCount == rR.TotalCount;
                _diag = _ok ? "match" : "DIVERGED";
            }
            catch (Exception e)
            {
                _diag = $"exception: {e.Message}";
            }
        }

        protected override ScenarioVerificationResult VerifyInternal(ScenarioParameterOverrides overrides)
            => new(new List<ScenarioVerificationResult.CheckResult>
            {
                new("two pipelines on identical trajectory produce identical results",
                    _ok, $"diag: {_diag}"),
            });

        protected override void OnCleanup() { }

        private sealed class VisualHarness
        {
            public ScenarioEventBus EventBus;
            public ScenarioGameObjectFactory Factory;
            public TerrainService Terrain;
            public TrackPieceCatalog Pieces;
            public TrackShapeCatalog Shapes;
            public TrackPlacementService Placement;
            public ShapePreviewService Preview;
            public ShapePlacementService ShapePlacement;
            public ShapePlacementPipeline Pipeline;
            public PipelineSession Session;
            public UnityEngine.GameObject GhostRoot;
        }

        private VisualHarness BuildHarness(string label)
        {
            var h = new VisualHarness
            {
                EventBus = new ScenarioEventBus(),
                Factory = new ScenarioGameObjectFactory(),
            };
            h.Terrain = new TerrainService(h.EventBus);
            h.Terrain.Initialize(new TerrainBuildOptions(20, 12, 0, TrackPieceConstants.CellSize));

            h.Pieces = new TrackPieceCatalog();
            TrackPieceCatalogSeeder.Seed(h.Pieces);
            h.Shapes = new TrackShapeCatalog();
            TrackShapeCatalogSeeder.Seed(h.Shapes, h.Pieces);

            var validators = new List<ITrackPlacementValidator>
            {
                new BoundsValidator(),
                new OverlapValidator(),
                new TerrainCompatibilityValidator(),
            };
            h.Placement = new TrackPlacementService(
                h.EventBus, h.Pieces, new TrackPieceMeshBuilder(new FlatHeightAdapter()),
                validators, h.Terrain, h.Factory, TrackPalette.Default);
            h.Preview = new ShapePreviewService(h.Pieces, validators, h.Terrain, h.Placement);
            h.ShapePlacement = new ShapePlacementService(h.EventBus, h.Preview, h.Placement);
            h.Pipeline = new ShapePlacementPipeline(
                h.EventBus, h.Preview, h.ShapePlacement, h.Placement,
                h.Pieces, new TrackPieceMeshBuilder(new FlatHeightAdapter()), h.Factory);

            h.GhostRoot = h.Factory.CreateEmpty($"[Parity-{label}] GhostRoot");
            h.GhostRoot.transform.SetParent(SceneRoot.transform, false);
            h.Session = h.Pipeline.Begin(new PipelineConfig(
                GhostRoot: h.GhostRoot,
                MagnetSnapRadius: 0.5f * TrackPieceConstants.CellSize,
                MagnetReleaseRadius: 0.9f * TrackPieceConstants.CellSize,
                ValidColor: new Color(0.2f, 0.9f, 0.3f, 0.55f),
                InvalidColor: new Color(0.95f, 0.25f, 0.25f, 0.55f),
                MagnetColor: new Color(0.1f, 0.85f, 0.85f, 0.7f),
                Terrain: h.Terrain,
                Animate: false));
            return h;
        }

        private static ShapePlacementResult DriveOnce(VisualHarness h, TrackShape shape, GridPosition cell, TrackDirection facing)
        {
            h.Pipeline.SetShape(h.Session, shape, TrackPieceVariantId.Default);
            h.Pipeline.SetFacing(h.Session, facing);
            float cs = TrackPieceConstants.CellSize;
            var worldHit = new Vector3((cell.X + 0.5f) * cs, 0f, (cell.Y + 0.5f) * cs);
            h.Pipeline.Tick(h.Session, cell, worldHit, cursorOnGrid: true, deltaTime: 0.016f);
            return h.Pipeline.Commit(h.Session);
        }

        private static TrackShape PickShape(TrackShapeCatalog cat)
        {
            TrackShape best = null;
            int min = int.MaxValue;
            foreach (var s in cat.All)
                if (s.Pieces.Count < min) { min = s.Pieces.Count; best = s; }
            return best;
        }
    }
}
