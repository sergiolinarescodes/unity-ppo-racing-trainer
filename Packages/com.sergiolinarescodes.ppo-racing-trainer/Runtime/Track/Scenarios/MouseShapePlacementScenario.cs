using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.Terrain;
using UnityPpoRacingTrainer.Core.Terrain.Generators;
using UnityPpoRacingTrainer.Core.Terrain.Scenarios;
using UnityPpoRacingTrainer.Core.Track.Loop;
using UnityPpoRacingTrainer.Core.Track.Presentation;
using UnityPpoRacingTrainer.Core.Track.Ribbon;
using UnityPpoRacingTrainer.Core.Track.Shape;
using Unidad.Core.Abstractions;
using Unidad.Core.Grid;
using Unidad.Core.Testing;
using UnityEngine;
using UnityEngine.InputSystem;

namespace UnityPpoRacingTrainer.Core.Track.Scenarios
{
    /// <summary>
    /// Live, mouse-driven shape placement on a 25×25 GentleSlope terrain. Acts as a
    /// thin coordinator: per frame it pulls a tile from <see cref="MouseInputAdapter"/>,
    /// asks <see cref="SnapResolverService"/> for an optional magnet snap, feeds the
    /// result into <see cref="PlacementStateMachine"/>, and asks
    /// <see cref="GhostPreviewRenderer"/> to refresh the visuals only when state
    /// changes. Left-click commits via <see cref="ShapePlacementService"/>.
    /// </summary>
    internal sealed class MouseShapePlacementScenario : DataDrivenScenario, ITickable
    {
        private const int TerrainWidth = 25;
        private const int TerrainDepth = 25;
        private const int TerrainSeed = 12345;
        private const int TerrainMaxLevel = 2; // mild slopes: 0, 0.5, 1.0 world units
        // Snap radii are in world units; scale with the global cell size so they stay
        // ~half-tile / ~one-tile in cell-relative terms regardless of grid scale.
        private const float MagnetSnapRadius = 0.5f * TrackPieceConstants.CellSize;
        private const float MagnetReleaseRadius = 0.9f * TrackPieceConstants.CellSize;

        private static readonly Color GhostValidColor = new(0.20f, 0.90f, 0.30f, 0.55f);
        private static readonly Color GhostInvalidColor = new(0.95f, 0.25f, 0.25f, 0.55f);
        private static readonly Color GhostMagnetColor = new(0.10f, 0.85f, 0.85f, 0.70f); // teal accent when snapped
        private static readonly Color MagnetMarkerColor = new(0.10f, 0.85f, 0.85f, 1f);

        private TerrainColorMode _terrainColorMode = TerrainColorMode.Palette;

        // -- service graph --
        private ScenarioEventBus _eventBus;
        private ScenarioGameObjectFactory _factory;
        private TerrainService _terrain;
        private TerrainMeshBuilder _terrainMeshBuilder;
        private TrackPieceCatalog _pieceCatalog;
        private TrackPieceMeshBuilder _pieceMeshBuilder;
        private List<ITrackPlacementValidator> _validators;
        private TrackPlacementService _placement;
        private TrackRibbonService _ribbonService;
        private ClosedLoopService _loopService;
        private TrackShapeCatalog _shapeCatalog;
        private ShapePreviewService _previewService;
        private ShapePlacementService _shapePlacementService;
        private ShapeCycleService _cycleService;

        // -- presentation helpers --
        // Shared placement core (preview + snap + ghost + state machine) lives in
        // IShapePlacementPipeline now — the scenario no longer wires snap/state/ghost
        // directly. Same building core as the main-scene Shift+D authoring input.
        private ShapePlacementPipeline _pipeline;
        private PipelineSession _session;
        private MouseInputAdapter _inputAdapter;
        private CameraPresetManager _cameraManager;

        // -- presentation --
        private GameObject _terrainObject;
        private Mesh _terrainMesh;
        private Material _terrainMaterial;
        private GameObject _ghostRoot;
        private GameObject _placedRoot;
        private MouseShapePlacementTickProxy _tickProxy;
        private Camera _camera;
        private bool _lastMagnet;

        private GameObject _magnetMarker;
        private Material _magnetMarkerMaterial;
        private int _anchorCycleIndex;
        private int _lastVariantCount;

        // V-key cycling: per-shape decoration variant (no walls / west wall / both / kerbs etc).
        // Independent of the snap-anchor variant cycling above (R-key) — that picks WHICH
        // boundary port to snap to; this picks WHAT walls/kerbs the placed piece carries.
        private TrackPieceVariantId _shapeVariantId;
        private int _shapeVariantMaxCount = 1;

        private readonly List<IDisposable> _subs = new();
        private int _placedShapesCount;
        private int _rejectedShapesCount;

        // Undo/redo stacks for committed shapes. One commit = one entry. Z pops
        // and removes the placed pieces; Y replays the same shape via TryPlaceShape.
        private readonly Stack<ShapePlaceCommand> _undoStack = new();
        private readonly Stack<ShapePlaceCommand> _redoStack = new();

        public MouseShapePlacementScenario() : base(new TestScenarioDefinition(
            "mouse-shape-placement",
            "Mouse Shape Placement (Live)",
            $"Interactive scenario. {TerrainWidth}×{TerrainDepth} GentleSlope terrain; cursor drives a ghost preview of " +
            "the active Track Shape. Per-piece tint is green when valid, red when a validator rejects the tile. " +
            "Left-click commits if all pieces are valid; Tab cycles to the next preset.",
            Array.Empty<ScenarioParameter>()))
        { }

        protected override void ExecuteInternal(ScenarioParameterOverrides overrides)
        {
            _placedShapesCount = 0;
            _rejectedShapesCount = 0;

            _eventBus = new ScenarioEventBus();
            _factory = new ScenarioGameObjectFactory();

            BuildTerrain();
            BuildTrackServices();
            BuildShapeServices();

            _placedRoot = _factory.CreateEmpty("[Mouse] PlacedPieces");
            _placedRoot.transform.SetParent(SceneRoot.transform, false);
            _ghostRoot = _factory.CreateEmpty("[Mouse] GhostRoot");
            _ghostRoot.transform.SetParent(SceneRoot.transform, false);

            _pipeline = new ShapePlacementPipeline(
                _eventBus, _previewService, _shapePlacementService, _placement,
                _pieceCatalog, _pieceMeshBuilder, _factory);
            _session = _pipeline.Begin(new PipelineConfig(
                GhostRoot: _ghostRoot,
                MagnetSnapRadius: MagnetSnapRadius,
                MagnetReleaseRadius: MagnetReleaseRadius,
                ValidColor: GhostValidColor,
                InvalidColor: GhostInvalidColor,
                MagnetColor: GhostMagnetColor,
                Terrain: _terrain,
                Animate: true));

            _camera = Camera.main;
            _cameraManager = new CameraPresetManager(_camera);
            ApplyIsoCamera();

            _inputAdapter = new MouseInputAdapter(_camera, _terrainObject, _terrain);

            HookTickProxy();

            _subs.Add(_eventBus.Subscribe<TrackShapePlacedEvent>(OnShapePlaced));
            _subs.Add(_eventBus.Subscribe<TrackShapePlacementRejectedEvent>(OnShapeRejected));
            _subs.Add(_eventBus.Subscribe<TrackShapeSelectedEvent>(OnShapeSelected));
            _subs.Add(_eventBus.Subscribe<TrackPiecePlacedEvent>(OnPiecePlaced));

            ResetShapeVariant();
            Debug.Log($"[MouseShapePlacement] Ready. Active shape: {_cycleService.Current?.Id} " +
                      $"({_cycleService.Current?.Pieces.Count} pieces). Tab cycles, R rotates / cycles snap-anchor, V cycles wall/kerb variant, left-click places.");
        }

        private void BuildTerrain()
        {
            _terrain = new TerrainService(_eventBus);
            _terrain.Initialize(new TerrainBuildOptions(TerrainWidth, TerrainDepth, 0, TrackPieceConstants.CellSize));

            int cw = _terrain.CornerWidth;
            int cd = _terrain.CornerDepth;
            var levels = new int[cw, cd];
            TerrainGenerators.Fill(TerrainGeneratorMode.GentleSlope, levels, TerrainSeed, TerrainMaxLevel);

            var heights = new float[cw, cd];
            for (int z = 0; z < cd; z++)
                for (int x = 0; x < cw; x++)
                    heights[x, z] = TerrainShapeRules.ToHeight(levels[x, z]);
            if (!_terrain.TrySetAllCorners(heights))
                Debug.LogError("[MouseShapePlacement] Terrain corner application failed.");

            _terrainMeshBuilder = new TerrainMeshBuilder();
            _terrainMesh = _terrainMeshBuilder.Build(_terrain, TerrainPalette.BoneAndSlate, _terrainColorMode);

            _terrainMaterial = ResolveOpaqueMaterial("MouseShape_TerrainMaterial");
            _terrainObject = _factory.CreateEmpty("[Mouse] Terrain");
            _terrainObject.transform.SetParent(SceneRoot.transform, false);
            _terrainObject.transform.position = Vector3.zero;

            var mf = _terrainObject.AddComponent<MeshFilter>();
            mf.sharedMesh = _terrainMesh;
            var mr = _terrainObject.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _terrainMaterial;
            var mc = _terrainObject.AddComponent<MeshCollider>();
            mc.sharedMesh = _terrainMesh;
        }

        private void BuildTrackServices()
        {
            _pieceCatalog = new TrackPieceCatalog();
            TrackPieceCatalogSeeder.Seed(_pieceCatalog);

            _pieceMeshBuilder = new TrackPieceMeshBuilder(new FlatHeightAdapter());

            _validators = new List<ITrackPlacementValidator>
            {
                new BoundsValidator(),
                new OverlapValidator(),
                new TerrainCompatibilityValidator()
            };

            _placement = new TrackPlacementService(
                _eventBus, _pieceCatalog, _pieceMeshBuilder,
                _validators, _terrain, _factory, TrackPalette.Default);

            _ribbonService = new TrackRibbonService(
                _eventBus, _placement, _pieceCatalog,
                _terrain, _factory, TrackPalette.Default);

            // Closed-loop service auto-subscribes to TrackPiecePlaced/Removed
            // events so the player gets the SAME start-line + sector posts the
            // trainer renders the moment their authored chain closes a loop.
            _loopService = new ClosedLoopService(_eventBus, _placement, _pieceCatalog, _terrain);
            SectorBoundaryDebugRenderer.MountOn(SceneRoot, _loopService);
        }

        private void BuildShapeServices()
        {
            _shapeCatalog = new TrackShapeCatalog();
            // The mouse-placement card pool is sourced exclusively from authored
            // partial tracks saved in the track editor (F2). The 36 hand-coded
            // recipes from TrackShapeCatalogSeeder live on for training/generation
            // scenarios but are intentionally absent here — the player curates
            // the playable card library themselves.
            int authored = AuthoredCardLoader.LoadAllInto(_shapeCatalog, _pieceCatalog);
            if (authored == 0)
            {
                Debug.LogWarning(
                    "[MouseShapePlacement] No authored cards on disk. Open the Track Editor scenario, " +
                    "build a partial segment, press F2 to save, then re-enter this scenario.");
            }

            _previewService = new ShapePreviewService(_pieceCatalog, _validators, _terrain, _placement);
            _shapePlacementService = new ShapePlacementService(_eventBus, _previewService, _placement);
            _cycleService = new ShapeCycleService(_eventBus, _shapeCatalog);
        }

        private void ApplyIsoCamera()
        {
            float c = TrackPieceConstants.CellSize;
            float cx = TerrainWidth * 0.5f * c;
            float cz = TerrainDepth * 0.5f * c;
            _cameraManager.ApplyLookAt(
                new Vector3(cx + 18f * c, 22f * c, cz - 18f * c),
                new Vector3(cx, 0f, cz));
        }

        private void HookTickProxy()
        {
            var proxyGo = _factory.CreateEmpty("[Mouse] TickProxy");
            proxyGo.transform.SetParent(SceneRoot.transform, false);
            _tickProxy = proxyGo.AddComponent<MouseShapePlacementTickProxy>();
            _tickProxy.OnTick = Tick;
        }

        public void Tick(float deltaTime)
        {
            if (!_inputAdapter.IsAvailable) return;

            _cameraManager?.Tick(deltaTime);
            // Push the latest cycle/variant state into the pipeline session every
            // frame so any cycle/variant key press lands before the Tick call resolves
            // the magnet candidate set.
            _pipeline.SetShape(_session, _cycleService.Current, _shapeVariantId);
            _pipeline.SetFacing(_session, _cycleService.Facing);
            HandleKeyboard();

            if (!_inputAdapter.TryGetTileUnderMouse(out var freeOrigin, out var worldHit))
            {
                _pipeline.Tick(_session, default, default, cursorOnGrid: false, deltaTime);
                HideMagnetMarker();
                return;
            }

            var frame = _pipeline.Tick(_session, freeOrigin, worldHit, cursorOnGrid: true, deltaTime);
            _lastVariantCount = ComputeMagnetVariantCount();

            if (frame.MagnetActive)
            {
                // Pipeline's session doesn't expose the matched OpenPort directly
                // (the magnet marker is presentation-only and scenario-specific).
                // Use the resolved origin's cell-center for the marker — it lands
                // on the same port the pipeline locked onto.
                UpdateMagnetMarker(OriginToWorld(frame.Origin));
            }

            if (frame.MagnetActive != _lastMagnet)
            {
                _lastMagnet = frame.MagnetActive;
                if (_lastMagnet)
                    Debug.Log($"[MouseShapePlacement] Magnet engaged origin={frame.Origin} facing={frame.Facing} shape={frame.Shape?.Id.Id}");
                else
                {
                    HideMagnetMarker();
                    _anchorCycleIndex = 0;
                    Debug.Log("[MouseShapePlacement] Magnet released.");
                }
            }

            HandleClick(frame);
        }

        // Cell-center world position for the magnet marker. Matches the math the
        // pipeline's snap resolver uses internally.
        private Vector3 OriginToWorld(GridPosition origin)
        {
            float c = _terrain.IsInitialized ? _terrain.CellSize : TrackPieceConstants.CellSize;
            return new Vector3((origin.X + 0.5f) * c, 0.05f, (origin.Y + 0.5f) * c);
        }

        // Anchor cycle bound for R-key-while-magnet. Re-walks boundary ports for the
        // active shape so the variant count matches what the pipeline saw internally
        // — they share the same SnapResolverService rules via ShapeBoundaryPorts.
        private int ComputeMagnetVariantCount()
        {
            var shape = _cycleService.Current;
            if (shape == null) return 0;
            var ports = ShapeBoundaryPorts.Enumerate(shape, _pieceCatalog);
            return ports.Count;
        }

        private void HandleKeyboard()
        {
            var kbd = Keyboard.current;
            if (kbd == null) return;
            if (kbd.tabKey.wasPressedThisFrame)
            {
                _cycleService.Next();
                _anchorCycleIndex = 0;
                ResetShapeVariant();
                _pipeline.SetShape(_session, _cycleService.Current, _shapeVariantId);
            }
            if (kbd.rKey.wasPressedThisFrame)
            {
                if (_lastMagnet)
                {
                    int mod = _lastVariantCount > 0 ? _lastVariantCount : 1;
                    _anchorCycleIndex = (_anchorCycleIndex + 1) % mod;
                    _pipeline.CycleAnchor(_session, +1);
                    Debug.Log($"[MouseShapePlacement] Magnet R → variant {_anchorCycleIndex}/{mod}");
                }
                else
                {
                    _cycleService.RotateRight();
                    _pipeline.SetFacing(_session, _cycleService.Facing);
                    Debug.Log($"[MouseShapePlacement] Rotated → facing {_cycleService.Facing}");
                }
            }
            if (kbd.vKey.wasPressedThisFrame)
            {
                CycleShapeVariant();
            }
            if (kbd.tKey.wasPressedThisFrame)
            {
                _terrainColorMode = _terrainColorMode == TerrainColorMode.Palette
                    ? TerrainColorMode.Categories
                    : TerrainColorMode.Palette;
                _terrainMeshBuilder.Rebuild(_terrainMesh, _terrain, TerrainPalette.BoneAndSlate, _terrainColorMode);
                Debug.Log($"[MouseShapePlacement] Terrain debug overlay: {_terrainColorMode} " +
                          "(Categories: flat/cardinal-ramp/angle-slope coloured by category).");
            }

            // Z = revert last placed card (removes every piece committed in that
            // click); Y = redo. Mirrors the track editor's keybinds.
            if (kbd.zKey.wasPressedThisFrame) Undo();
            if (kbd.yKey.wasPressedThisFrame) Redo();
        }

        /// <summary>
        /// Variant max for the current card = max VariantCount across every piece in the
        /// shape (a card may mix piece types; the cycle wraps at the most-variant piece's
        /// count and pieces with fewer variants clamp to their default via GetVariant).
        /// </summary>
        private int ComputeShapeVariantMaxCount()
        {
            var shape = _cycleService?.Current;
            if (shape == null || _pieceCatalog == null) return 1;
            int max = 1;
            for (int i = 0; i < shape.Pieces.Count; i++)
            {
                if (_pieceCatalog.TryGet(shape.Pieces[i].PieceType, out var def))
                    if (def.VariantCount > max) max = def.VariantCount;
            }
            return max;
        }

        private void ResetShapeVariant()
        {
            _shapeVariantId = TrackPieceVariantId.Default;
            _shapeVariantMaxCount = ComputeShapeVariantMaxCount();
        }

        private void CycleShapeVariant()
        {
            if (_shapeVariantMaxCount <= 1)
            {
                _shapeVariantMaxCount = ComputeShapeVariantMaxCount();
                if (_shapeVariantMaxCount <= 1)
                {
                    Debug.Log($"[MouseShapePlacement] V → no variants on shape '{_cycleService.Current?.Id.Id}'");
                    return;
                }
            }
            _shapeVariantId = _shapeVariantId.Next(_shapeVariantMaxCount);
            _pipeline.SetShape(_session, _cycleService.Current, _shapeVariantId);
            Debug.Log($"[MouseShapePlacement] V → variant {_shapeVariantId.Index}/{_shapeVariantMaxCount} on shape '{_cycleService.Current?.Id.Id}'");
        }

        private void HandleClick(PipelineFrame frame)
        {
            if (!Mouse.current.leftButton.wasPressedThisFrame) return;
            if (!frame.HasPreview) return;

            // Magnet-gate: the very first card lands wherever the cursor is. Every
            // subsequent card MUST be magnet-snapped to an existing piece's open
            // port — same connectivity rule the track editor uses, prevents
            // disconnected island chains in the placed network.
            if (_placement.Placed.Count > 0 && !frame.MagnetActive)
            {
                Debug.Log("[MouseShapePlacement] Click ignored — first card is free, " +
                          "subsequent cards must magnet-snap to an existing piece's open port.");
                return;
            }

            var result = _pipeline.Commit(_session);
            if (!result.Success) return;

            _undoStack.Push(new ShapePlaceCommand(frame.Shape, frame.Origin, frame.Facing, _shapeVariantId, result.PieceIds));
            _redoStack.Clear();
        }

        private void Undo()
        {
            if (_undoStack.Count == 0) { Debug.Log("[MouseShapePlacement] Nothing to undo."); return; }
            var cmd = _undoStack.Pop();
            for (int i = 0; i < cmd.PlacedIds.Count; i++) _placement.Remove(cmd.PlacedIds[i]);
            cmd.PlacedIds.Clear();
            _redoStack.Push(cmd);
            _pipeline.Invalidate(_session);
            Debug.Log("[MouseShapePlacement] Undo");
        }

        private void Redo()
        {
            if (_redoStack.Count == 0) { Debug.Log("[MouseShapePlacement] Nothing to redo."); return; }
            var cmd = _redoStack.Pop();
            var result = _shapePlacementService.TryPlaceShape(cmd.Shape, cmd.Origin, cmd.Facing, cmd.VariantId, animate: true);
            if (!result.Success)
            {
                Debug.LogWarning($"[MouseShapePlacement] Redo rejected: {result.InvalidCount}/{result.TotalCount} invalid.");
                return;
            }
            cmd.PlacedIds.Clear();
            for (int i = 0; i < result.PieceIds.Count; i++) cmd.PlacedIds.Add(result.PieceIds[i]);
            _undoStack.Push(cmd);
            _pipeline.Invalidate(_session);
            Debug.Log("[MouseShapePlacement] Redo");
        }

        private sealed class ShapePlaceCommand
        {
            public readonly TrackShape Shape;
            public readonly GridPosition Origin;
            public readonly TrackDirection Facing;
            public readonly TrackPieceVariantId VariantId;
            public readonly List<TrackPieceId> PlacedIds = new();

            public ShapePlaceCommand(
                TrackShape shape, GridPosition origin, TrackDirection facing,
                TrackPieceVariantId variantId, IReadOnlyList<TrackPieceId> placedIds)
            {
                Shape = shape; Origin = origin; Facing = facing; VariantId = variantId;
                for (int i = 0; i < placedIds.Count; i++) PlacedIds.Add(placedIds[i]);
            }
        }

        private void EnsureMagnetMarker()
        {
            if (_magnetMarker != null) return;
            _magnetMarker = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            _magnetMarker.name = "[Mouse] MagnetMarker";
            _magnetMarker.transform.SetParent(SceneRoot.transform, false);
            _magnetMarker.transform.localScale = new Vector3(0.18f, 0.04f, 0.18f);
            var col = _magnetMarker.GetComponent<Collider>();
            if (col != null) UnityEngine.Object.DestroyImmediate(col);

            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            _magnetMarkerMaterial = new Material(shader) { name = "MagnetMarker_Mat" };
            if (_magnetMarkerMaterial.HasProperty("_BaseColor")) _magnetMarkerMaterial.SetColor("_BaseColor", MagnetMarkerColor);
            if (_magnetMarkerMaterial.HasProperty("_Color")) _magnetMarkerMaterial.SetColor("_Color", MagnetMarkerColor);
            _magnetMarker.GetComponent<MeshRenderer>().sharedMaterial = _magnetMarkerMaterial;
            _magnetMarker.SetActive(false);
        }

        private void UpdateMagnetMarker(Vector3 portWorldPosition)
        {
            EnsureMagnetMarker();
            _magnetMarker.transform.position = portWorldPosition + new Vector3(0f, 0.05f, 0f);
            if (!_magnetMarker.activeSelf) _magnetMarker.SetActive(true);
        }

        private void HideMagnetMarker()
        {
            if (_magnetMarker != null && _magnetMarker.activeSelf) _magnetMarker.SetActive(false);
        }

        private static Material ResolveOpaqueMaterial(string name)
        {
            var shader = Shader.Find("RaceConstructor/TerrainVertexColor")
                         ?? Shader.Find("Universal Render Pipeline/Lit");
            return new Material(shader) { name = name };
        }

        private void OnShapePlaced(TrackShapePlacedEvent e)
        {
            _placedShapesCount++;
            Debug.Log($"[MouseShapePlacement] Placed shape {e.ShapeId.Id} @ {e.Origin}, {e.PieceIds.Count} pieces.");
        }

        private void OnShapeRejected(TrackShapePlacementRejectedEvent e)
        {
            _rejectedShapesCount++;
            Debug.Log($"[MouseShapePlacement] Rejected shape {e.ShapeId.Id} @ {e.Origin}: {e.InvalidCount}/{e.TotalCount} invalid.");
        }

        private void OnShapeSelected(TrackShapeSelectedEvent e) =>
            Debug.Log($"[MouseShapePlacement] Selected shape #{e.Index}: {e.ShapeId.Id}");

        private void OnPiecePlaced(TrackPiecePlacedEvent e)
        {
            var go = _placement.GetGameObject(e.Id);
            if (go != null) go.transform.SetParent(_placedRoot.transform, true);
        }

        protected override ScenarioVerificationResult VerifyInternal(ScenarioParameterOverrides overrides)
        {
            var checks = new List<ScenarioVerificationResult.CheckResult>
            {
                new("Terrain initialised at 25×25",
                    _terrain != null && _terrain.IsInitialized && _terrain.Width == TerrainWidth && _terrain.Depth == TerrainDepth,
                    null),
                new("Piece catalog populated by seeder",
                    _pieceCatalog != null && _pieceCatalog.Count > 0,
                    $"got {_pieceCatalog?.Count}"),
                // Authored card pool — Count is whatever the user has saved on disk
                // via the track editor. Empty (Count == 0) is a legal v1 state and
                // surfaces an empty-state warning at scenario start; a populated
                // catalog comes from AuthoredCardLoader.LoadAllInto over the JSON
                // store. The scenario does NOT call TrackShapeCatalogSeeder.
                new("Shape catalog initialised (authored-only pool)",
                    _shapeCatalog != null,
                    $"got {_shapeCatalog?.Count}"),
                new("Ghost root parented under SceneRoot",
                    _ghostRoot != null && _ghostRoot.transform.parent == SceneRoot.transform,
                    null),
                // Cycle service must handle Count==0 cleanly — Current is null,
                // Tab is no-op. With cards on disk, Current is non-null and at index 0.
                new("Cycle service initialised (handles empty catalog)",
                    _cycleService != null
                        && _cycleService.CurrentIndex == 0
                        && (_shapeCatalog.Count == 0
                            ? _cycleService.Current == null
                            : _cycleService.Current != null),
                    null),
                new("Terrain has MeshCollider",
                    _terrainObject != null && _terrainObject.GetComponent<MeshCollider>() != null,
                    null),
                new("Camera available for raycasting",
                    _camera != null,
                    null),
                new("Tick proxy attached",
                    _tickProxy != null,
                    null),
                new("Subscriptions registered (4 event types)",
                    _subs.Count == 4,
                    $"got {_subs.Count}"),
                new("Presentation helpers wired",
                    _pipeline != null && _session != null && _inputAdapter != null && _cameraManager != null,
                    null),
            };
            return new ScenarioVerificationResult(checks);
        }

        protected override void OnCleanup()
        {
            for (int i = 0; i < _subs.Count; i++) _subs[i].Dispose();
            _subs.Clear();

            if (_tickProxy != null) _tickProxy.OnTick = null;

            if (_session != null) { _pipeline?.End(_session); _session = null; }
            _ribbonService?.Dispose();
            _loopService?.Dispose();
            _placement?.Dispose();

            if (_terrainMesh != null) UnityEngine.Object.DestroyImmediate(_terrainMesh);
            if (_terrainMaterial != null) UnityEngine.Object.DestroyImmediate(_terrainMaterial);
            if (_magnetMarkerMaterial != null) UnityEngine.Object.DestroyImmediate(_magnetMarkerMaterial);

            _cameraManager?.Restore();

            _factory?.Dispose();
            _eventBus?.ClearAllSubscriptions();

            _eventBus = null;
            _factory = null;
            _terrain = null;
            _terrainMeshBuilder = null;
            _pieceCatalog = null;
            _pieceMeshBuilder = null;
            _validators = null;
            _placement = null;
            _ribbonService = null;
            _shapeCatalog = null;
            _previewService = null;
            _shapePlacementService = null;
            _cycleService = null;

            _pipeline = null;
            _session = null;
            _inputAdapter = null;
            _cameraManager = null;

            _terrainObject = null;
            _terrainMesh = null;
            _terrainMaterial = null;
            _ghostRoot = null;
            _placedRoot = null;
            _tickProxy = null;
            _camera = null;
            _lastMagnet = false;
            _magnetMarker = null;
            _magnetMarkerMaterial = null;
            _anchorCycleIndex = 0;
            _shapeVariantId = TrackPieceVariantId.Default;
            _shapeVariantMaxCount = 1;

            _placedShapesCount = 0;
            _rejectedShapesCount = 0;
        }
    }
}
