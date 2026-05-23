using System;
using System.Collections.Generic;
using System.IO;
using UnityPpoRacingTrainer.Core.Terrain;
using UnityPpoRacingTrainer.Core.Terrain.Generators;
using UnityPpoRacingTrainer.Core.Terrain.Scenarios;
using UnityPpoRacingTrainer.Core.Track.Authoring.Drag;
using UnityPpoRacingTrainer.Core.Track.Presentation;
using UnityPpoRacingTrainer.Core.Track.Shape;
using Unidad.Core.Abstractions;
using Unidad.Core.Grid;
using Unidad.Core.Testing;
using UnityEngine;
using UnityEngine.InputSystem;

namespace UnityPpoRacingTrainer.Core.Track.Authoring
{
    // -------------------------------------------------------------------------
    // RACING in-game track editor (Cities-Skylines style drag-to-build).
    //
    // PURPOSE: author REUSABLE PARTIAL TRACK SEGMENTS that become the playable
    // card library for MouseShapePlacementScenario. The editor does NOT build
    // closed loops. Each F2 save publishes a partial chain as a card; the
    // sidebar IS the card library. AuthoredCardLoader loads these into the
    // mouse-placement catalog at scenario entry — they're the SOLE shape
    // source for that scenario (the 36 hand-coded TrackShapeCatalogSeeder
    // recipes are out of scope here).
    //
    // The player clicks-and-drags to lay road. The drag's direction is inferred
    // from cursor MOTION — every new tile the cursor dwells in for ~150 ms is
    // promoted to a waypoint; fast flicks through intermediate cells are
    // ignored so a NE-direction drag that grazes (1,0) on its way to (1,1)
    // emits a single diagonal step, not a cardinal east step. Mid-drag
    // direction changes auto-inject the matching curve / transition piece.
    // Mouse-up commits the chain through ITrackPlacementService — one drag =
    // one undo entry.
    //
    // After the first piece is placed, the drag must START on (or within snap
    // radius of) one of the existing track's open ports. Lattice mismatches
    // between the anchor and the drag direction are bridged automatically —
    // the player never picks the connector.
    //
    // Inputs:
    //   LMB-DRAG — author a chain of road; release to commit
    //   RMB      — cycle the variant of the placed piece under the cursor
    //   Esc      — cancel the in-progress drag
    //   Z        — undo last drag (removes every piece committed in that drag)
    //   Y        — redo
    //   WASD     — pan camera in world XZ (Shift = ×3 fast-pan)
    //   Scroll   — zoom (dolly camera along its forward direction)
    //   [ / ]    — rotate the entire composite track ±90° around its centroid
    //   F2       — save the current partial as a card
    //   F3       — reload the most recent saved card (uses converter →
    //              IShapePlacementService.TryPlaceShape, same path mouse-
    //              placement uses, single placement code path)
    //
    // All other types in this file are private/internal helpers co-located to
    // minimize csproj-regen friction (per project memory).
    // -------------------------------------------------------------------------
    internal sealed class TrackPartsEditorScenario : DataDrivenScenario, ITickable
    {
        private const int TerrainWidth = 16;
        private const int TerrainDepth = 16;
        private const int TerrainSeed = 7777;
        private const float MagnetSnapRadius = 0.5f * TrackPieceConstants.CellSize;
        private const float MagnetReleaseRadius = 0.9f * TrackPieceConstants.CellSize;

        private static readonly Color GhostValidColor = new(0.20f, 0.90f, 0.30f, 0.55f);
        private static readonly Color GhostInvalidColor = new(0.95f, 0.25f, 0.25f, 0.55f);
        private static readonly Color GhostMagnetColor = new(0.10f, 0.85f, 0.85f, 0.70f);

        // Starter palette. First four are the original "small straight / small
        // diagonal / small curve / large curve" set; the trailing five expose the
        // new diagonal-mating curves so the editor can author rect↔diagonal flow
        // in a single piece.
        private static readonly TrackPieceShape[] StarterPieces =
        {
            TrackPieceShapes.Straight_1x1,
            TrackPieceShapes.Straight_1x2,
            TrackPieceShapes.Straight_Diag_1x1,
            TrackPieceShapes.Curve_1x1,
            TrackPieceShapes.LeftCurve_1x1,
            TrackPieceShapes.Curve_Long_1x2,
            TrackPieceShapes.CurveDiagTransition_1x1,
            TrackPieceShapes.CurveDiagTransitionLeft_1x1,
            TrackPieceShapes.CurveDiagToCardinal_1x1,
            TrackPieceShapes.CurveDiagToCardinalLeft_1x1,
            TrackPieceShapes.CurveDiagHairpin_1x1,
        };

        // -- service graph (mirrors MouseShapePlacementScenario) --
        private ScenarioEventBus _eventBus;
        private ScenarioGameObjectFactory _factory;
        private TerrainService _terrain;
        private TerrainMeshBuilder _terrainMeshBuilder;
        private TrackPieceCatalog _pieceCatalog;
        private TrackPieceMeshBuilder _pieceMeshBuilder;
        private List<ITrackPlacementValidator> _validators;
        private TrackPlacementService _placement;
        private SnapResolverService _snapResolver;
        private ShapePreviewService _previewService;
        private ShapePlacementService _shapePlacementService;
        private TrackShapeCatalog _shapeCatalog;
        private GhostPreviewRenderer _ghostRenderer;
        private MouseInputAdapter _inputAdapter;
        private CameraPresetManager _cameraManager;
        private PlacementStateMachine _stateMachine;
        private PlacementHistoryService _history;

        // -- drag-build (Cities-Skylines style) --
        private TerrainLatticeClassifier _lattice;
        private GreedyChainDiscretizer _discretizer;
        private DragBuildSession _dragSession;
        private OpenPortIndex _dragOpenPortIndex;
        private bool _dragOpenPortIndexDirty = true;

        // -- presentation --
        private GameObject _terrainObject;
        private Mesh _terrainMesh;
        private Material _terrainMaterial;
        private GameObject _ghostRoot;
        private GameObject _placedRoot;
        private TrackPartsEditorTickProxy _tickProxy;
        private Camera _camera;
        private SavedTracksPanel _savedTracksPanel;

        // -- editor state --
        private int _starterIndex;
        private TrackShape _activeStarter;
        private TrackDirection _facing = TrackDirection.North;
        private TrackPieceVariantId _variantId = TrackPieceVariantId.Default;
        private int _anchorCycleIndex;
        private ShapePreviewResult? _cachedPreview;
        private float _compositeYaw; // accumulated composite rotation in degrees
        private bool _isInteractive;  // input only fires when scenario is the focused, active scene
        private readonly List<IDisposable> _subs = new();

        public TrackPartsEditorScenario() : base(new TestScenarioDefinition(
            "track-editor",
            "Track Editor (Magnet-Snap, Live)",
            $"Author a closed track interactively. Starter pieces: small straight, small diagonal, small curve, " +
            $"large curve. After the first piece, the cursor must magnet-snap to an open port of an existing piece " +
            $"for a click to commit. Space cycles starter, R rotates / cycles anchor variant, V cycles wall variants, " +
            $"[ / ] rotates the composite, Ctrl+Z/Y undo/redo, F2 saves, F3 loads.",
            Array.Empty<ScenarioParameter>()))
        { }

        protected override void ExecuteInternal(ScenarioParameterOverrides overrides)
        {
            _eventBus = new ScenarioEventBus();
            _factory = new ScenarioGameObjectFactory();

            BuildTerrain();
            BuildTrackServices();

            _placedRoot = _factory.CreateEmpty("[Editor] PlacedPieces");
            _placedRoot.transform.SetParent(SceneRoot.transform, false);
            _ghostRoot = _factory.CreateEmpty("[Editor] GhostRoot");
            _ghostRoot.transform.SetParent(SceneRoot.transform, false);

            _ghostRenderer = new GhostPreviewRenderer(
                _pieceCatalog, _pieceMeshBuilder, _factory, _ghostRoot,
                GhostValidColor, GhostInvalidColor, GhostMagnetColor);

            _camera = Camera.main;
            _cameraManager = new CameraPresetManager(_camera);
            ApplyIsoCamera();

            _inputAdapter = new MouseInputAdapter(_camera, _terrainObject, _terrain);
            _stateMachine = new PlacementStateMachine();
            _history = new PlacementHistoryService(_placement, _eventBus);

            HookTickProxy();

            _subs.Add(_eventBus.Subscribe<TrackPiecePlacedEvent>(OnPiecePlaced));
            _subs.Add(_eventBus.Subscribe<TrackPieceRemovedEvent>(_ => _stateMachine.Invalidate()));
            _subs.Add(_eventBus.Subscribe<TrackPiecePlacedEvent>(_ => _stateMachine.Invalidate()));
            _subs.Add(_eventBus.Subscribe<TrackPiecePlacedEvent>(_ => _dragOpenPortIndexDirty = true));
            _subs.Add(_eventBus.Subscribe<TrackPieceRemovedEvent>(_ => _dragOpenPortIndexDirty = true));

            SetStarter(0);

            _savedTracksPanel = new SavedTracksPanel(
                onLoad: LoadAuthoredTrackById,
                onDuplicate: DuplicateAuthoredTrack,
                onDelete: DeleteAuthoredTrack);
            RootVisualElement.Add(_savedTracksPanel);
            _savedTracksPanel.Refresh();

            Debug.Log($"[TrackEditor] Ready. {StarterPieces.Length} starters. " +
                      "First click places freely; after that NEW PIECES MUST MAGNET-SNAP to an existing piece's open port.");
        }

        private void BuildTerrain()
        {
            _terrain = new TerrainService(_eventBus);
            _terrain.Initialize(new TerrainBuildOptions(TerrainWidth, TerrainDepth, 0, TrackPieceConstants.CellSize));

            int cw = _terrain.CornerWidth;
            int cd = _terrain.CornerDepth;
            var levels = new int[cw, cd]; // flat — editor focuses on layout, not topo
            TerrainGenerators.Fill(TerrainGeneratorMode.Plains, levels, TerrainSeed, 0);

            var heights = new float[cw, cd];
            for (int z = 0; z < cd; z++)
                for (int x = 0; x < cw; x++)
                    heights[x, z] = TerrainShapeRules.ToHeight(levels[x, z]);
            _terrain.TrySetAllCorners(heights);

            _terrainMeshBuilder = new TerrainMeshBuilder();
            _terrainMesh = _terrainMeshBuilder.Build(_terrain, TerrainPalette.BoneAndSlate, TerrainColorMode.Palette);

            _terrainMaterial = ResolveOpaqueMaterial("Editor_TerrainMaterial");
            _terrainObject = _factory.CreateEmpty("[Editor] Terrain");
            _terrainObject.transform.SetParent(SceneRoot.transform, false);

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

            _snapResolver = new SnapResolverService(_eventBus, _placement, _pieceCatalog, _terrain);

            _shapeCatalog = new TrackShapeCatalog();
            // No built-in shape seeding — the editor uses one-piece shapes built on demand.

            _previewService = new ShapePreviewService(_pieceCatalog, _validators, _terrain, _placement);
            _shapePlacementService = new ShapePlacementService(_eventBus, _previewService, _placement);

            // Drag-build wiring — Cities-Skylines style click-and-drag input.
            _lattice = new TerrainLatticeClassifier(_terrain);
            _discretizer = new GreedyChainDiscretizer();
            _dragSession = new DragBuildSession(_placement, _discretizer, _lattice, _terrain);
            _dragOpenPortIndex = new OpenPortIndex();
        }

        private void ApplyIsoCamera()
        {
            float c = TrackPieceConstants.CellSize;
            float cx = TerrainWidth * 0.5f * c;
            float cz = TerrainDepth * 0.5f * c;
            _cameraManager.ApplyLookAt(
                new Vector3(cx + 14f * c, 18f * c, cz - 14f * c),
                new Vector3(cx, 0f, cz));
        }

        private void HookTickProxy()
        {
            var proxyGo = _factory.CreateEmpty("[Editor] TickProxy");
            proxyGo.transform.SetParent(SceneRoot.transform, false);
            _tickProxy = proxyGo.AddComponent<TrackPartsEditorTickProxy>();
            _tickProxy.OnTick = Tick;
        }

        private void SetStarter(int index)
        {
            _starterIndex = ((index % StarterPieces.Length) + StarterPieces.Length) % StarterPieces.Length;
            var pieceShape = StarterPieces[_starterIndex];
            _activeStarter = SinglePieceShape.Wrap(pieceShape);
            _variantId = TrackPieceVariantId.Default;
            _anchorCycleIndex = 0;
            _stateMachine.Invalidate();
            int variantCount = _pieceCatalog.TryGet(pieceShape, out var d) ? d.VariantCount : 1;
            string family = d != null ? d.Family.ToString() : "?";
            string dim = d != null ? $"{d.Dimensions.Width}x{d.Dimensions.Length}" : "?";
            Debug.Log($"[TrackEditor] Starter [{_starterIndex + 1}/{StarterPieces.Length}] → '{pieceShape.Id}' family={family} dim={dim} variants={variantCount}");
        }

        public void Tick(float deltaTime)
        {
            // Re-evaluated each tick. Without this, Ctrl+Z fired in the Game scene
            // (or while another scenario is in the foreground) would still hit the
            // editor's history stack since the tick proxy is alive in the inactive
            // SceneRoot. Gate ALL input — keyboard AND mouse — on this flag.
            _isInteractive = SceneRoot != null && SceneRoot.activeInHierarchy && Application.isFocused;
            if (!_isInteractive) return;
            if (!_inputAdapter.IsAvailable) return;
            _cameraManager?.Tick(deltaTime);
            HandleKeyboard();

            if (!_inputAdapter.TryGetTileUnderMouse(out _, out var worldHit))
            {
                if (_dragSession.State != DragSessionState.Dragging) _ghostRenderer.HideAll();
                return;
            }

            HandleDrag(worldHit, deltaTime);
        }

        /// <summary>
        /// Cities-Skylines style drag-to-build flow. Mouse-down begins a session
        /// (anchored to the nearest open port if any pieces exist), mouse-move
        /// extends the drag, mouse-up commits the discretized chain.
        /// </summary>
        private void HandleDrag(Vector3 worldHit, float deltaTime)
        {
            var mouse = Mouse.current;
            if (mouse == null) return;

            EnsureDragOpenPortIndex();

            if (mouse.leftButton.wasPressedThisFrame && _dragSession.State == DragSessionState.Idle)
            {
                OpenPort? anchor = null;
                if (_placement.Placed.Count > 0)
                {
                    if (_dragOpenPortIndex.TryFindNearest(worldHit, MagnetReleaseRadius, out var op))
                        anchor = op;
                    else
                    {
                        Debug.Log("[TrackEditor] Drag click ignored — must start near an existing piece's open port.");
                        return;
                    }
                }
                if (!_dragSession.TryBegin(anchor, worldHit))
                {
                    Debug.Log("[TrackEditor] Drag begin rejected (terrain forbidden or out of bounds).");
                }
                else
                {
                    string a = anchor.HasValue ? anchor.Value.OutwardDirection.ToString() : "free";
                    Debug.Log($"[TrackEditor] Drag started — anchor={a}");
                }
            }

            if (_dragSession.State == DragSessionState.Dragging)
            {
                _dragSession.UpdateCursor(worldHit);
                var preview = BuildDragPreviewResult(_dragSession.PreviewPieces);
                _ghostRenderer.Render(preview, _terrain, deltaTime, magnetActive: false, _variantId, forceSnap: false);
            }
            else
            {
                _ghostRenderer.HideAll();
            }

            if (mouse.leftButton.wasReleasedThisFrame && _dragSession.State == DragSessionState.Dragging)
            {
                // Snapshot the discretized pieces BEFORE Commit() — Commit clears
                // the session's internal preview list and we need the original
                // (Shape, Origin, Facing, Variant) tuples for Redo.
                var src = _dragSession.PreviewPieces;
                var snapshot = new DiscretizedPiece[src.Count];
                for (int i = 0; i < src.Count; i++) snapshot[i] = src[i];

                var result = _dragSession.Commit();
                if (result.Committed > 0) _history.RecordDragBatch(snapshot, result.Ids);

                Debug.Log($"[TrackEditor] Drag committed: {result.Committed}/{result.Requested} pieces.{(string.IsNullOrEmpty(result.Diagnostic) ? string.Empty : " diag=" + result.Diagnostic)}");
                _ghostRenderer.HideAll();
            }

            // Right-click on a placed piece cycles its variant in-place. v1 uses
            // a step-cycle (one click = one variant forward) instead of a popup
            // menu — keeps the gesture immediate and aligned with the existing V
            // key while still letting the player browse all variants per piece.
            if (mouse.rightButton.wasPressedThisFrame && _dragSession.State == DragSessionState.Idle)
                CyclePlacedPieceVariantUnderCursor();
        }

        private void CyclePlacedPieceVariantUnderCursor()
        {
            if (!_inputAdapter.TryGetTileUnderMouse(out var tile, out _)) return;
            // Find the placed piece occupying this tile (single-piece footprints
            // are dominant; for multi-tile pieces we resolve to the anchor tile).
            if (!_placement.Occupancy.TryGetValue(tile, out var pieceId)) return;
            if (!_placement.TryGetPiece(pieceId, out var piece)) return;
            if (!_pieceCatalog.TryGet(piece.Shape, out var def) || def.VariantCount <= 1)
            {
                Debug.Log($"[TrackEditor] Piece '{piece.Shape.Id}' has no alternate variants.");
                return;
            }
            var nextVariant = piece.VariantId.Next(def.VariantCount);
            // Re-place: remove + TryPlace preserves origin, facing, shape; only
            // VariantId changes. Done in one undo entry by going through history.
            _placement.Remove(pieceId);
            var result = _placement.TryPlace(piece.Shape, piece.Origin, piece.Facing, nextVariant);
            if (result.Success)
            {
                var v = def.GetVariant(nextVariant);
                Debug.Log($"[TrackEditor] Variant cycled '{piece.Shape.Id}' @({piece.Origin.X},{piece.Origin.Y}) → [{nextVariant.Index + 1}/{def.VariantCount}] '{v.DisplayName}'");
            }
            else
            {
                // Restore original on failure so the player doesn't see a
                // disappearing piece.
                _placement.TryPlace(piece.Shape, piece.Origin, piece.Facing, piece.VariantId);
                Debug.LogWarning($"[TrackEditor] Variant cycle failed: {result.Reason}. Restored original.");
            }
        }

        private void EnsureDragOpenPortIndex()
        {
            if (!_dragOpenPortIndexDirty) return;
            _dragOpenPortIndex.Rebuild(_placement.Placed, _pieceCatalog, _terrain.CellSize);
            _dragOpenPortIndexDirty = false;
        }

        private ShapePreviewResult BuildDragPreviewResult(IReadOnlyList<DiscretizedPiece> pieces)
        {
            var slots = new List<PiecePreview>(pieces.Count);
            bool allValid = true;
            for (int i = 0; i < pieces.Count; i++)
            {
                var p = pieces[i];
                var single = SinglePieceShape.Wrap(p.Shape);
                var r = _previewService.Compute(single, p.Origin, p.Facing);
                if (r.Pieces.Count > 0) slots.Add(r.Pieces[0]);
                if (!r.AllValid) allValid = false;
            }
            return new ShapePreviewResult(default, default, slots, allValid);
        }

        private void HandleKeyboard()
        {
            var kbd = Keyboard.current;
            if (kbd == null) return;

            if (kbd.spaceKey.wasPressedThisFrame) SetStarter(_starterIndex + 1);

            if (kbd.rKey.wasPressedThisFrame)
            {
                if (_stateMachine.MagnetActive)
                {
                    _anchorCycleIndex++;
                    _stateMachine.Invalidate();
                    Debug.Log($"[TrackEditor] Anchor variant cycle → {_anchorCycleIndex}");
                }
                else
                {
                    // 45° step so diagonal pieces (NE/SE/SW/NW facings) are reachable.
                    _facing = _facing.RotateRight45();
                    _stateMachine.Invalidate();
                    Debug.Log($"[TrackEditor] Facing → {_facing}");
                }
            }

            if (kbd.vKey.wasPressedThisFrame) CycleVariant();

            // Plain Z / Y for undo / redo (no Ctrl modifier — quicker for editor flow).
            // Each completed drag pushes one batch onto the history stack; Z removes
            // the entire batch at once.
            if (kbd.zKey.wasPressedThisFrame)
                Debug.Log(_history.Undo() ? "[TrackEditor] Undo" : "[TrackEditor] Nothing to undo.");
            if (kbd.yKey.wasPressedThisFrame)
                Debug.Log(_history.Redo() ? "[TrackEditor] Redo" : "[TrackEditor] Nothing to redo.");

            if (kbd.leftBracketKey.wasPressedThisFrame) RotateComposite(-90f);
            if (kbd.rightBracketKey.wasPressedThisFrame) RotateComposite(+90f);

            if (kbd.f2Key.wasPressedThisFrame) SaveAuthoredTrack();
            if (kbd.f3Key.wasPressedThisFrame) LoadAuthoredTrack();

            if (kbd.escapeKey.wasPressedThisFrame) _dragSession?.Cancel();
        }

        private void CycleVariant()
        {
            if (_activeStarter == null || _activeStarter.Pieces.Count == 0) return;
            if (!_pieceCatalog.TryGet(_activeStarter.Pieces[0].PieceType, out var def)) return;
            _variantId = _variantId.Next(def.VariantCount);
            _stateMachine.Invalidate();
            var variant = def.GetVariant(_variantId);
            string edges = SummarizeEdges(variant.Edges);
            Debug.Log($"[TrackEditor] Variant [{_variantId.Index + 1}/{def.VariantCount}] '{def.Shape.Id}' → '{variant.DisplayName}' shoulder={variant.WallShoulderMode} edges=[{edges}]");
        }

        private static string SummarizeEdges(IReadOnlyList<EdgeMarker> edges)
        {
            if (edges == null || edges.Count == 0) return "none";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < edges.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                var e = edges[i];
                sb.Append(e.Kind).Append('@').Append(e.Anchor);
                if (e.TileIndex != 0) sb.Append("(t").Append(e.TileIndex).Append(')');
            }
            return sb.ToString();
        }

        /// <summary>
        /// Rotates every placed piece's GameObject + the composite parent by
        /// <paramref name="degrees"/> around the centroid of placed pieces. Visual-
        /// only for now; a full bake (mutate origin/facing per piece) happens on save
        /// via <see cref="RotationBaker.Bake"/>.
        /// </summary>
        private void RotateComposite(float degrees)
        {
            _compositeYaw = Mathf.Repeat(_compositeYaw + degrees, 360f);
            _placedRoot.transform.rotation = Quaternion.Euler(0f, _compositeYaw, 0f);
            Debug.Log($"[TrackEditor] Composite yaw → {_compositeYaw}°");
        }

        private void RebuildPreview(TrackShape shape, GridPosition origin, TrackDirection facing)
        {
            _cachedPreview = _previewService.Compute(shape, origin, facing);
        }

        private void OnPiecePlaced(TrackPiecePlacedEvent e)
        {
            var go = _placement.GetGameObject(e.Id);
            if (go != null) go.transform.SetParent(_placedRoot.transform, true);
        }

        private void SaveAuthoredTrack()
        {
            var baked = RotationBaker.Bake(_placement.Placed, _compositeYaw);
            string id = $"track_{DateTime.Now:yyyyMMdd_HHmmss}";
            var pieces = new AuthoredTrackPiece[baked.Count];
            for (int i = 0; i < baked.Count; i++)
            {
                pieces[i] = new AuthoredTrackPiece
                {
                    ShapeId = baked[i].Shape.Id,
                    X = baked[i].X,
                    Y = baked[i].Y,
                    Facing = (int)baked[i].Facing,
                    VariantIndex = baked[i].VariantIndex,
                };
            }
            AuthoredTrackJsonStore.Save(id, id, pieces);
            Debug.Log($"[TrackEditor] Saved authored track '{id}' ({baked.Count} pieces, yaw={_compositeYaw}°).");
            _savedTracksPanel?.Refresh();
        }

        private void LoadAuthoredTrack()
        {
            // F3 = quick-load most recent. Sidebar exposes the per-track loader.
            var ids = AuthoredTrackJsonStore.ListIds();
            if (ids.Count == 0)
            {
                Debug.LogWarning("[TrackEditor] No saved tracks.");
                return;
            }
            string newest = ids[0];
            for (int i = 1; i < ids.Count; i++)
                if (string.CompareOrdinal(ids[i], newest) > 0) newest = ids[i];
            LoadAuthoredTrackById(newest);
        }

        internal void LoadAuthoredTrackById(string id)
        {
            var data = AuthoredTrackJsonStore.Load(id);
            if (data == null)
            {
                Debug.LogWarning($"[TrackEditor] No saved track with id '{id}'.");
                return;
            }
            _placement.Clear();
            _history.Reset();
            _compositeYaw = 0f;
            if (_placedRoot != null) _placedRoot.transform.rotation = Quaternion.identity;

            if (data.Pieces == null || data.Pieces.Length == 0)
            {
                Debug.LogWarning($"[TrackEditor] '{id}' has no pieces.");
                return;
            }

            // Run reload through the same TrackShape pipeline mouse-placement uses.
            // Origin is restored to the saved min-corner so absolute positions match
            // what the user authored — the converter shifts piece offsets to (0,0)
            // so we shift back here.
            int minX = int.MaxValue, minY = int.MaxValue;
            for (int i = 0; i < data.Pieces.Length; i++)
            {
                if (data.Pieces[i].X < minX) minX = data.Pieces[i].X;
                if (data.Pieces[i].Y < minY) minY = data.Pieces[i].Y;
            }

            TrackShape shape;
            try { shape = AuthoredTrackToShapeConverter.Convert(data, _pieceCatalog); }
            catch (AuthoredTrackConversionException e)
            {
                Debug.LogWarning($"[TrackEditor] Cannot reload '{id}': {e.Message}");
                return;
            }

            var result = _shapePlacementService.TryPlaceShape(
                shape,
                new GridPosition(minX, minY),
                TrackDirection.North,
                TrackPieceVariantId.Default);

            if (!result.Success)
            {
                Debug.LogWarning(
                    $"[TrackEditor] Reload of '{id}' rejected: {result.TotalCount - result.InvalidCount}/{result.TotalCount} placeable.");
                return;
            }
            Debug.Log($"[TrackEditor] Loaded '{id}' ({result.PieceIds.Count} pieces via TrackShape).");
        }

        internal void DuplicateAuthoredTrack(string id)
        {
            var data = AuthoredTrackJsonStore.Load(id);
            if (data == null) return;
            string copyId = NextAvailableCopyId(id);
            AuthoredTrackJsonStore.Save(copyId, copyId, data.Pieces);
            Debug.Log($"[TrackEditor] Duplicated '{id}' → '{copyId}'.");
            _savedTracksPanel?.Refresh();
        }

        internal void DeleteAuthoredTrack(string id)
        {
            string path = AuthoredTrackJsonStore.PathFor(id);
            if (!File.Exists(path)) return;
            File.Delete(path);
            Debug.Log($"[TrackEditor] Deleted '{id}'.");
            _savedTracksPanel?.Refresh();
        }

        private static string NextAvailableCopyId(string baseId)
        {
            var existing = AuthoredTrackJsonStore.ListIds();
            var seen = new HashSet<string>(existing);
            for (int n = 1; n < 1000; n++)
            {
                string candidate = $"{baseId}_copy{n}";
                if (!seen.Contains(candidate)) return candidate;
            }
            return $"{baseId}_copy{Guid.NewGuid().ToString("N").Substring(0, 6)}";
        }

        private static Material ResolveOpaqueMaterial(string name)
        {
            var shader = Shader.Find("RaceConstructor/TerrainVertexColor")
                         ?? Shader.Find("Universal Render Pipeline/Lit");
            return new Material(shader) { name = name };
        }

        protected override ScenarioVerificationResult VerifyInternal(ScenarioParameterOverrides overrides)
        {
            var checks = new List<ScenarioVerificationResult.CheckResult>
            {
                new("Terrain ready", _terrain != null && _terrain.IsInitialized, null),
                new("Piece catalog seeded", _pieceCatalog != null && _pieceCatalog.Count > 0, null),
                new("History service ready", _history != null, null),
                new("Active starter set", _activeStarter != null, null),
                new("Tick proxy attached", _tickProxy != null, null),
                new("Subscriptions registered", _subs.Count == 5, $"got {_subs.Count}"),
            };
            return new ScenarioVerificationResult(checks);
        }

        protected override void OnCleanup()
        {
            for (int i = 0; i < _subs.Count; i++) _subs[i].Dispose();
            _subs.Clear();
            if (_tickProxy != null) _tickProxy.OnTick = null;
            _snapResolver?.Dispose();
            _ghostRenderer?.Dispose();
            _placement?.Dispose();
            if (_terrainMesh != null) UnityEngine.Object.DestroyImmediate(_terrainMesh);
            if (_terrainMaterial != null) UnityEngine.Object.DestroyImmediate(_terrainMaterial);
            _factory?.Dispose();
            _eventBus?.ClearAllSubscriptions();

            _eventBus = null; _factory = null; _terrain = null; _terrainMeshBuilder = null;
            _pieceCatalog = null; _pieceMeshBuilder = null; _validators = null;
            _placement = null; _snapResolver = null; _previewService = null;
            _shapePlacementService = null; _shapeCatalog = null;
            _ghostRenderer = null; _inputAdapter = null; _cameraManager = null;
            _stateMachine = null; _history = null;
            _terrainObject = null; _terrainMesh = null; _terrainMaterial = null;
            _ghostRoot = null; _placedRoot = null;
            _tickProxy = null; _camera = null;
            _savedTracksPanel = null;
            _activeStarter = null; _facing = TrackDirection.North;
            _variantId = TrackPieceVariantId.Default;
            _anchorCycleIndex = 0;
            _cachedPreview = null; _compositeYaw = 0f; _starterIndex = 0;
        }
    }

    /// <summary>MonoBehaviour proxy that forwards Update ticks into the scenario's <c>Tick</c>.</summary>
    internal sealed class TrackPartsEditorTickProxy : MonoBehaviour
    {
        public Action<float> OnTick;
        private void Update() { OnTick?.Invoke(Time.deltaTime); }
    }

    /// <summary>
    /// Wraps a single <see cref="TrackPieceShape"/> into a <see cref="TrackShape"/>
    /// so the editor can feed it through the existing shape preview / placement
    /// pipeline. The wrapped shape has exactly one <see cref="TrackShapePiece"/>
    /// at the origin, facing north.
    /// </summary>
    internal static class SinglePieceShape
    {
        public static TrackShape Wrap(TrackPieceShape shape)
        {
            var pieces = new[] { new TrackShapePiece(shape, GridOffset.Zero, TrackDirection.North) };
            return new TrackShape(new TrackShapeId($"editor.{shape.Id}"), $"editor.{shape.Id}", pieces);
        }
    }

    /// <summary>
    /// One drag commit captured for undo/redo. Stores the original discretized
    /// pieces (so Redo can replay the same chain) plus the live placed ids (so
    /// Undo can remove them). The piece list and id list are not kept in sync
    /// 1:1 — a piece may have failed validation at commit time and be absent
    /// from <see cref="PlacedIds"/> while still listed in <see cref="Pieces"/>;
    /// Redo handles this gracefully by retrying every piece and rebuilding ids.
    /// </summary>
    internal sealed class DragBatchCommand
    {
        public readonly DiscretizedPiece[] Pieces;
        public readonly List<TrackPieceId> PlacedIds = new();

        public DragBatchCommand(
            DiscretizedPiece[] pieces,
            IReadOnlyList<TrackPieceId> initialIds)
        {
            Pieces = pieces;
            for (int i = 0; i < initialIds.Count; i++) PlacedIds.Add(initialIds[i]);
        }
    }

    /// <summary>
    /// Linear undo/redo stack over <see cref="DragBatchCommand"/>. One drag =
    /// one entry. The redo branch is dropped on any new placement (standard
    /// editor semantics). On Redo, ids are reissued by replaying TryPlace —
    /// piece ids change across cycles but the geometry is identical.
    /// </summary>
    internal sealed class PlacementHistoryService
    {
        private readonly TrackPlacementService _placement;
        private readonly Stack<DragBatchCommand> _undo = new();
        private readonly Stack<DragBatchCommand> _redo = new();
#pragma warning disable CS0414 // reserved for future history events
        private readonly Unidad.Core.EventBus.IEventBus _eventBus;
#pragma warning restore CS0414

        public PlacementHistoryService(TrackPlacementService placement, Unidad.Core.EventBus.IEventBus eventBus)
        {
            _placement = placement; _eventBus = eventBus;
        }

        /// <summary>
        /// Capture a successful drag commit so it can be undone. No-op if either
        /// argument is empty (zero-piece drag = nothing to undo).
        /// </summary>
        public void RecordDragBatch(
            DiscretizedPiece[] pieces,
            IReadOnlyList<TrackPieceId> placedIds)
        {
            if (pieces == null || pieces.Length == 0) return;
            if (placedIds == null || placedIds.Count == 0) return;
            _undo.Push(new DragBatchCommand(pieces, placedIds));
            _redo.Clear();
        }

        public bool Undo()
        {
            if (_undo.Count == 0) return false;
            var cmd = _undo.Pop();
            for (int i = 0; i < cmd.PlacedIds.Count; i++) _placement.Remove(cmd.PlacedIds[i]);
            cmd.PlacedIds.Clear();
            _redo.Push(cmd);
            return true;
        }

        public bool Redo()
        {
            if (_redo.Count == 0) return false;
            var cmd = _redo.Pop();
            cmd.PlacedIds.Clear();
            for (int i = 0; i < cmd.Pieces.Length; i++)
            {
                var dp = cmd.Pieces[i];
                var r = _placement.TryPlace(dp.Shape, dp.Origin, dp.Facing, dp.Variant);
                if (r.Success) cmd.PlacedIds.Add(r.Id);
            }
            _undo.Push(cmd);
            return true;
        }

        public void Reset()
        {
            _undo.Clear(); _redo.Clear();
        }
    }

    /// <summary>
    /// Bakes the visual composite-yaw rotation into per-piece (origin, facing)
    /// tuples on save, so loading the JSON elsewhere reconstructs the same layout
    /// without needing to remember the parent transform's rotation. Composite
    /// rotation is constrained to ±90° steps so the bake stays integer-grid clean.
    /// </summary>
    internal static class RotationBaker
    {
        public static List<AuthoredPiece> Bake(IReadOnlyCollection<TrackPiece> pieces, float yawDegrees)
        {
            var list = new List<AuthoredPiece>(pieces.Count);
            int turns = Mathf.RoundToInt(yawDegrees / 90f);
            turns = ((turns % 4) + 4) % 4;
            int cx = 0, cy = 0;
            foreach (var p in pieces) { cx += p.Origin.X; cy += p.Origin.Y; }
            if (pieces.Count > 0) { cx /= pieces.Count; cy /= pieces.Count; }
            foreach (var p in pieces)
            {
                int rx = p.Origin.X - cx, ry = p.Origin.Y - cy;
                // 90° CW grid rotation: (x,y) → (y, -x).
                for (int t = 0; t < turns; t++) { int nx = ry; int ny = -rx; rx = nx; ry = ny; }
                var facing = p.Facing;
                for (int t = 0; t < turns; t++) facing = facing.RotateRight();
                list.Add(new AuthoredPiece
                {
                    Shape = p.Shape,
                    X = rx + cx,
                    Y = ry + cy,
                    Facing = facing,
                    VariantIndex = p.VariantId.Index,
                });
            }
            return list;
        }
    }

    /// <summary>
    /// Bake-only DTO used by <see cref="RotationBaker"/>. Disk persistence goes
    /// through <see cref="AuthoredTrackJsonStore"/> in <c>Core.Track.Shape</c>.
    /// </summary>
    internal struct AuthoredPiece
    {
        public TrackPieceShape Shape;
        public int X;
        public int Y;
        public TrackDirection Facing;
        public byte VariantIndex;
    }

    /// <summary>
    /// Left-side sidebar listing every saved authored track. Each row shows a
    /// top-down ASCII-style sketch of the layout plus Load / Duplicate / Delete
    /// buttons. Programmatic <see cref="UnityEngine.UIElements.VisualElement"/>
    /// tree (no UXML) — matches <c>HeuristicLapScenario</c>'s panel idiom.
    /// </summary>
    internal sealed class SavedTracksPanel : UnityEngine.UIElements.VisualElement
    {
        private readonly Action<string> _onLoad;
        private readonly Action<string> _onDuplicate;
        private readonly Action<string> _onDelete;
        private readonly UnityEngine.UIElements.ScrollView _list;

        public SavedTracksPanel(Action<string> onLoad, Action<string> onDuplicate, Action<string> onDelete)
        {
            _onLoad = onLoad;
            _onDuplicate = onDuplicate;
            _onDelete = onDelete;

            style.position = UnityEngine.UIElements.Position.Absolute;
            style.left = 8;
            style.top = 8;
            style.bottom = 8;
            style.width = 240;
            style.backgroundColor = new Color(0.06f, 0.07f, 0.09f, 0.92f);
            style.paddingLeft = 8; style.paddingRight = 8;
            style.paddingTop = 8; style.paddingBottom = 8;
            style.borderTopLeftRadius = 6; style.borderTopRightRadius = 6;
            style.borderBottomLeftRadius = 6; style.borderBottomRightRadius = 6;

            var header = new UnityEngine.UIElements.Label("Card Library — Partial Segments");
            header.style.color = new Color(0.92f, 0.95f, 1f);
            header.style.fontSize = 14;
            header.style.unityFontStyleAndWeight = FontStyle.Bold;
            header.style.marginBottom = 6;
            Add(header);

            _list = new UnityEngine.UIElements.ScrollView(UnityEngine.UIElements.ScrollViewMode.Vertical)
            {
                name = "saved-tracks-list",
            };
            _list.style.flexGrow = 1;
            Add(_list);
        }

        public void Refresh()
        {
            _list.Clear();
            var ids = AuthoredTrackJsonStore.ListIds();
            if (ids.Count == 0)
            {
                var empty = new UnityEngine.UIElements.Label(
                    "No cards yet. Drag-build a partial segment, press F2 to publish it as a card. " +
                    "Saved cards become the playable pool in MouseShapePlacementScenario.");
                empty.style.color = new Color(0.65f, 0.68f, 0.72f);
                empty.style.whiteSpace = UnityEngine.UIElements.WhiteSpace.Normal;
                empty.style.fontSize = 11;
                _list.Add(empty);
                return;
            }

            // Newest first by lexical order — save id includes a timestamp so this is chronological.
            var sorted = new List<string>(ids);
            sorted.Sort((a, b) => string.CompareOrdinal(b, a));

            for (int i = 0; i < sorted.Count; i++)
            {
                string id = sorted[i];
                var data = AuthoredTrackJsonStore.Load(id);
                _list.Add(BuildRow(id, data));
            }
        }

        private UnityEngine.UIElements.VisualElement BuildRow(string id, AuthoredTrackData data)
        {
            var row = new UnityEngine.UIElements.VisualElement();
            row.style.marginBottom = 6;
            row.style.paddingTop = 6; row.style.paddingBottom = 6;
            row.style.paddingLeft = 6; row.style.paddingRight = 6;
            row.style.backgroundColor = new Color(0.10f, 0.12f, 0.16f, 0.85f);
            row.style.borderTopLeftRadius = 4; row.style.borderTopRightRadius = 4;
            row.style.borderBottomLeftRadius = 4; row.style.borderBottomRightRadius = 4;

            var thumb = new SavedTrackThumbnail(data);
            row.Add(thumb);

            var name = new UnityEngine.UIElements.Label(id);
            name.style.color = new Color(0.92f, 0.95f, 1f);
            name.style.fontSize = 11;
            name.style.marginTop = 4;
            name.style.unityTextAlign = TextAnchor.MiddleCenter;
            row.Add(name);

            int pieceCount = data?.Pieces?.Length ?? 0;
            var meta = new UnityEngine.UIElements.Label($"{pieceCount} piece{(pieceCount == 1 ? "" : "s")}");
            meta.style.color = new Color(0.65f, 0.68f, 0.72f);
            meta.style.fontSize = 9;
            meta.style.unityTextAlign = TextAnchor.MiddleCenter;
            row.Add(meta);

            var btnRow = new UnityEngine.UIElements.VisualElement();
            btnRow.style.flexDirection = UnityEngine.UIElements.FlexDirection.Row;
            btnRow.style.marginTop = 4;
            btnRow.Add(MakeButton("Load", () => _onLoad?.Invoke(id), new Color(0.10f, 0.55f, 0.85f)));
            btnRow.Add(MakeButton("Dup", () => _onDuplicate?.Invoke(id), new Color(0.18f, 0.55f, 0.30f)));
            btnRow.Add(MakeButton("Del", () => _onDelete?.Invoke(id), new Color(0.65f, 0.18f, 0.18f)));
            row.Add(btnRow);
            return row;
        }

        private static UnityEngine.UIElements.Button MakeButton(string text, Action onClick, Color tint)
        {
            var b = new UnityEngine.UIElements.Button(onClick) { text = text };
            b.style.flexGrow = 1;
            b.style.marginLeft = 0; b.style.marginRight = 0;
            b.style.marginTop = 0; b.style.marginBottom = 0;
            b.style.fontSize = 10;
            b.style.color = Color.white;
            b.style.backgroundColor = tint;
            b.style.paddingTop = 2; b.style.paddingBottom = 2;
            b.style.paddingLeft = 4; b.style.paddingRight = 4;
            b.style.borderTopWidth = 0; b.style.borderBottomWidth = 0;
            b.style.borderLeftWidth = 0; b.style.borderRightWidth = 0;
            return b;
        }
    }

    /// <summary>
    /// Top-down 64-tall sketch of a saved track that mirrors what the 3D mesh
    /// strategies would build. Curves render as actual arcs; straights as lines;
    /// walls render as solid grey bands offset outside the road on the canonical
    /// edge anchors stored in each piece's variant. Static kerbs were removed;
    /// kerb sketching is no longer done here (the runtime racing-line kerb service
    /// places them dynamically based on the ghost's lap).
    /// </summary>
    internal sealed class SavedTrackThumbnail : UnityEngine.UIElements.VisualElement
    {
        private readonly AuthoredTrackData _data;
        private static TrackPieceCatalog _sharedCatalog;

        // Visual offsets in grid-units. RoadHalfWidth is the apparent half-road
        // for the thumbnail (smaller than the 3D road so the bands read clearly
        // at 64px). Wall sits just outside the road edge.
        private const float RoadHalfWidth = 0.30f;
        private const float WallOffset = 0.10f;
        private const float WallExtraMid = 0.04f;
        private const int CurveSegSmall = 12;
        private const int CurveSegLarge = 24;

        private static readonly Color RoadColor = new(0.20f, 0.75f, 0.95f, 0.95f);
        private static readonly Color WallColor = new(0.55f, 0.58f, 0.62f, 1f);

        public SavedTrackThumbnail(AuthoredTrackData data)
        {
            _data = data;
            style.height = 64;
            style.backgroundColor = new Color(0.05f, 0.06f, 0.08f, 1f);
            generateVisualContent += OnGenerate;
        }

        private static TrackPieceCatalog SharedCatalog
        {
            get
            {
                if (_sharedCatalog != null) return _sharedCatalog;
                _sharedCatalog = new TrackPieceCatalog();
                TrackPieceCatalogSeeder.Seed(_sharedCatalog);
                return _sharedCatalog;
            }
        }

        private struct PieceGeom
        {
            public List<Vector2> Centerline;
            public List<List<Vector2>> Walls;
        }

        private void OnGenerate(UnityEngine.UIElements.MeshGenerationContext ctx)
        {
            if (_data == null || _data.Pieces == null || _data.Pieces.Length == 0) return;

            var geoms = new List<PieceGeom>(_data.Pieces.Length);
            float minX = float.PositiveInfinity, maxX = float.NegativeInfinity;
            float minY = float.PositiveInfinity, maxY = float.NegativeInfinity;
            for (int i = 0; i < _data.Pieces.Length; i++)
            {
                if (!BuildGeom(_data.Pieces[i], out var g)) continue;
                geoms.Add(g);
                ExpandBounds(g.Centerline, ref minX, ref maxX, ref minY, ref maxY);
                for (int k = 0; k < g.Walls.Count; k++) ExpandBounds(g.Walls[k], ref minX, ref maxX, ref minY, ref maxY);
            }
            if (geoms.Count == 0) return;

            float w = contentRect.width;
            float h = contentRect.height;
            if (w <= 0f || h <= 0f) return;

            const float pad = 4f;
            float spanX = Mathf.Max(0.5f, maxX - minX);
            float spanY = Mathf.Max(0.5f, maxY - minY);
            float scale = Mathf.Min((w - pad * 2f) / spanX, (h - pad * 2f) / spanY);
            float drawW = spanX * scale;
            float drawH = spanY * scale;
            float ox = (w - drawW) * 0.5f;
            float oy = (h - drawH) * 0.5f;

            float minXc = minX, maxYc = maxY;
            Vector2 ToPx(Vector2 grid)
                => new(ox + (grid.x - minXc) * scale, oy + (maxYc - grid.y) * scale);

            var painter = ctx.painter2D;
            painter.lineCap = UnityEngine.UIElements.LineCap.Round;
            painter.lineJoin = UnityEngine.UIElements.LineJoin.Round;

            // Pass 1 — walls (back).
            painter.lineWidth = Mathf.Max(2f, scale * 0.10f);
            painter.strokeColor = WallColor;
            for (int i = 0; i < geoms.Count; i++)
            {
                var ws = geoms[i].Walls;
                for (int j = 0; j < ws.Count; j++) StrokePolyline(painter, ws[j], ToPx);
            }

            // Pass 2 — road centerline (front).
            painter.lineWidth = Mathf.Max(2f, scale * 0.18f);
            painter.strokeColor = RoadColor;
            for (int i = 0; i < geoms.Count; i++)
                StrokePolyline(painter, geoms[i].Centerline, ToPx);
        }

        private static void ExpandBounds(List<Vector2> pts, ref float minX, ref float maxX, ref float minY, ref float maxY)
        {
            for (int i = 0; i < pts.Count; i++)
            {
                var p = pts[i];
                if (p.x < minX) minX = p.x; if (p.x > maxX) maxX = p.x;
                if (p.y < minY) minY = p.y; if (p.y > maxY) maxY = p.y;
            }
        }

        private static void StrokePolyline(UnityEngine.UIElements.Painter2D p, List<Vector2> pts, Func<Vector2, Vector2> toPx)
        {
            if (pts == null || pts.Count < 2) return;
            p.BeginPath();
            p.MoveTo(toPx(pts[0]));
            for (int i = 1; i < pts.Count; i++) p.LineTo(toPx(pts[i]));
            p.Stroke();
        }

        private bool BuildGeom(AuthoredTrackPiece piece, out PieceGeom g)
        {
            g = new PieceGeom
            {
                Centerline = new List<Vector2>(),
                Walls = new List<List<Vector2>>()
            };

            var shape = new TrackPieceShape(piece.ShapeId);
            if (!SharedCatalog.TryGet(shape, out var def)) return false;
            if (def.Ports == null || def.Ports.Count < 2) return false;

            int W = def.Dimensions.Width;
            int L = def.Dimensions.Length;
            byte vIdx = (byte)Mathf.Clamp(piece.VariantIndex, 0, byte.MaxValue);
            var variant = def.GetVariant(new TrackPieceVariantId(vIdx));
            float wallExtra = variant.WallShoulderMode == WallShoulderMode.Mid ? WallExtraMid : 0f;
            int facing = ((piece.Facing % 8) + 8) % 8;

            switch (def.Family)
            {
                case TrackPieceFamily.Curve:
                    if (def.Shape == TrackPieceShapes.Curve_Long_1x2) BuildLongCurve(def, variant, wallExtra, ref g);
                    else BuildQuarterCurve(def, variant, wallExtra, ref g);
                    break;
                case TrackPieceFamily.Straight:
                case TrackPieceFamily.Ramp:
                    BuildStraight(def, variant, wallExtra, ref g);
                    break;
                case TrackPieceFamily.DiagonalStraight:
                    BuildDiagonalStraight(def, variant, wallExtra, ref g);
                    break;
                case TrackPieceFamily.DiagonalCurve:
                    BuildDiagonalCurve(def, variant, wallExtra, ref g);
                    break;
                default:
                    BuildPortPolyline(def, ref g);
                    break;
            }

            // "Left" pieces (LCURVE_*, etc.) are stored as MirrorX'd of their canonical
            // counterpart — flip x = W - x in canonical space before rotating, so the
            // mirrored ports & edges land in the right world cells.
            if (def.MirrorX)
            {
                MirrorXList(g.Centerline, W);
                for (int i = 0; i < g.Walls.Count; i++) MirrorXList(g.Walls[i], W);
            }

            // Rotate around canonical bbox center (W/2, L/2), then translate.
            Vector2 pivot = new(W * 0.5f, L * 0.5f);
            Vector2 worldOrigin = new(piece.X + W * 0.5f, piece.Y + L * 0.5f);
            TransformList(g.Centerline, pivot, worldOrigin, facing);
            for (int i = 0; i < g.Walls.Count; i++) TransformList(g.Walls[i], pivot, worldOrigin, facing);
            return true;
        }

        // Canonical straight: road along +Z, lanes centered at x = W/2.
        private static void BuildStraight(TrackPieceDefinition def, TrackPieceVariant variant, float wallExtra, ref PieceGeom g)
        {
            int W = def.Dimensions.Width;
            int L = def.Dimensions.Length;
            float halfRoad = W * RoadHalfWidth;
            float centerX = W * 0.5f;
            float roadX0 = centerX - halfRoad;
            float roadX1 = centerX + halfRoad;

            g.Centerline.Add(new Vector2(centerX, 0f));
            g.Centerline.Add(new Vector2(centerX, L));

            if (variant.Edges == null) return;
            for (int i = 0; i < variant.Edges.Count; i++)
            {
                var e = variant.Edges[i];
                float off = WallOffset + wallExtra;
                var band = new List<Vector2>(2);
                switch (e.Anchor)
                {
                    case EdgeAnchor.StraightWest:
                    case EdgeAnchor.RampWest:
                        {
                            float x = roadX0 - off;
                            band.Add(new Vector2(x, Mathf.Lerp(0f, L, e.StartT)));
                            band.Add(new Vector2(x, Mathf.Lerp(0f, L, e.EndT)));
                            break;
                        }
                    case EdgeAnchor.StraightEast:
                    case EdgeAnchor.RampEast:
                        {
                            float x = roadX1 + off;
                            band.Add(new Vector2(x, Mathf.Lerp(0f, L, e.StartT)));
                            band.Add(new Vector2(x, Mathf.Lerp(0f, L, e.EndT)));
                            break;
                        }
                    case EdgeAnchor.StraightSouth:
                        {
                            float z = -off;
                            band.Add(new Vector2(Mathf.Lerp(roadX0, roadX1, e.StartT), z));
                            band.Add(new Vector2(Mathf.Lerp(roadX0, roadX1, e.EndT), z));
                            break;
                        }
                    case EdgeAnchor.StraightNorth:
                        {
                            float z = L + off;
                            band.Add(new Vector2(Mathf.Lerp(roadX0, roadX1, e.StartT), z));
                            band.Add(new Vector2(Mathf.Lerp(roadX0, roadX1, e.EndT), z));
                            break;
                        }
                    default:
                        continue;
                }
                g.Walls.Add(band);
            }
        }

        // Canonical NE quarter-arc (small 1×1 or large 2×2). Center at (W,0),
        // theta sweeps from π/2 (east-port) to π (south-port).
        private static void BuildQuarterCurve(TrackPieceDefinition def, TrackPieceVariant variant, float wallExtra, ref PieceGeom g)
        {
            int W = def.Dimensions.Width;
            float Rc = def.CurveCenterRadius > 0f ? def.CurveCenterRadius : W * 0.5f;
            float halfRoad = W * RoadHalfWidth;
            float Ri = Mathf.Max(0.01f, Rc - halfRoad);
            float Ro = Rc + halfRoad;
            Vector2 center = new(W, 0f);
            int seg = W >= 2 ? CurveSegLarge : CurveSegSmall;
            const float t0 = Mathf.PI * 0.5f;
            const float t1 = Mathf.PI;

            EmitArc(g.Centerline, center, Rc, t0, t1, seg);

            if (variant.Edges == null) return;
            for (int i = 0; i < variant.Edges.Count; i++)
            {
                var e = variant.Edges[i];
                if (e.Anchor != EdgeAnchor.ArcInner && e.Anchor != EdgeAnchor.ArcOuter) continue;
                float off = WallOffset + wallExtra;
                float r = e.Anchor == EdgeAnchor.ArcOuter ? Ro + off : Mathf.Max(0.01f, Ri - off);
                float a0 = Mathf.Lerp(t0, t1, e.StartT);
                float a1 = Mathf.Lerp(t0, t1, e.EndT);
                var band = new List<Vector2>(seg + 1);
                EmitArc(band, center, r, a0, a1, seg);
                g.Walls.Add(band);
            }
        }

        // Canonical 1×2 long curve: straight lead-in tile (z∈[0,1], road along
        // x=0.5±halfRoad) + NE quarter-arc tile centered at (1,1) with R=0.5.
        // Edges dispatch on TileIndex (0 = lead-in straight, 1 = arc tile).
        private static void BuildLongCurve(TrackPieceDefinition def, TrackPieceVariant variant, float wallExtra, ref PieceGeom g)
        {
            float halfRoad = RoadHalfWidth;
            float roadX0 = 0.5f - halfRoad;
            float roadX1 = 0.5f + halfRoad;
            float Rc = 0.5f;
            float Ri = Mathf.Max(0.01f, Rc - halfRoad);
            float Ro = Rc + halfRoad;
            Vector2 arcCenter = new(1f, 1f);
            int seg = CurveSegSmall;
            const float t0 = Mathf.PI * 0.5f;
            const float t1 = Mathf.PI;

            // Centerline: straight tile then arc.
            g.Centerline.Add(new Vector2(0.5f, 0f));
            g.Centerline.Add(new Vector2(0.5f, 1f));
            EmitArc(g.Centerline, arcCenter, Rc, t0, t1, seg);

            if (variant.Edges == null) return;
            for (int i = 0; i < variant.Edges.Count; i++)
            {
                var e = variant.Edges[i];
                float off = WallOffset + wallExtra;
                if (e.TileIndex == 0)
                {
                    var band = new List<Vector2>(2);
                    if (e.Anchor == EdgeAnchor.StraightWest)
                    {
                        float x = roadX0 - off;
                        band.Add(new Vector2(x, Mathf.Lerp(0f, 1f, e.StartT)));
                        band.Add(new Vector2(x, Mathf.Lerp(0f, 1f, e.EndT)));
                    }
                    else if (e.Anchor == EdgeAnchor.StraightEast)
                    {
                        float x = roadX1 + off;
                        band.Add(new Vector2(x, Mathf.Lerp(0f, 1f, e.StartT)));
                        band.Add(new Vector2(x, Mathf.Lerp(0f, 1f, e.EndT)));
                    }
                    else continue;
                    g.Walls.Add(band);
                }
                else // TileIndex == 1: arc tile
                {
                    if (e.Anchor != EdgeAnchor.ArcInner && e.Anchor != EdgeAnchor.ArcOuter) continue;
                    float r = e.Anchor == EdgeAnchor.ArcOuter ? Ro + off : Mathf.Max(0.01f, Ri - off);
                    float a0 = Mathf.Lerp(t0, t1, e.StartT);
                    float a1 = Mathf.Lerp(t0, t1, e.EndT);
                    var band = new List<Vector2>(seg + 1);
                    EmitArc(band, arcCenter, r, a0, a1, seg);
                    g.Walls.Add(band);
                }
            }
        }

        // Canonical diagonal straight: 1×1 tile, road runs corner-to-corner from
        // SW (0,0) to NE (W,L). Wall bands run parallel to that diagonal at
        // ±(halfRoad + WallOffset) along the perpendicular axis. Matches
        // DiagonalStraightMeshStrategy's canonical orientation.
        private static void BuildDiagonalStraight(TrackPieceDefinition def, TrackPieceVariant variant, float wallExtra, ref PieceGeom g)
        {
            int W = def.Dimensions.Width;
            int L = def.Dimensions.Length;
            g.Centerline.Add(new Vector2(0f, 0f));
            g.Centerline.Add(new Vector2(W, L));

            if (variant.Edges == null) return;
            const float K = 0.7071067811865475f; // 1/√2
            Vector2 rightPerp = new(K, -K);
            Vector2 leftPerp = new(-K, K);
            float off = (W * RoadHalfWidth) + WallOffset + wallExtra;
            for (int i = 0; i < variant.Edges.Count; i++)
            {
                var e = variant.Edges[i];
                Vector2 perp;
                if (e.Anchor == EdgeAnchor.DiagonalRight) perp = rightPerp;
                else if (e.Anchor == EdgeAnchor.DiagonalLeft) perp = leftPerp;
                else continue;
                Vector2 endpoint = new(W, L);
                Vector2 start = endpoint * e.StartT;
                Vector2 end = endpoint * e.EndT;
                var band = new List<Vector2>(2)
                {
                    start + perp * off,
                    end + perp * off,
                };
                g.Walls.Add(band);
            }
        }

        // Canonical diagonal-curve family. Mirrors DiagonalCurveMeshStrategy:
        // each shape has a (startPos, startRight) and (endPos, endRight) pair; a
        // Hermite spline with port-tangent handles draws the centerline. Walls
        // for DiagonalLeft / DiagonalRight ride along the same spline offset by
        // ±(halfRoad + WallOffset) along the per-sample right-perpendicular.
        // Mirror ("Left") shapes are drawn canonically here; SavedTrackThumbnail
        // then applies the MirrorX flip at BuildGeom L1142.
        private static void BuildDiagonalCurve(TrackPieceDefinition def, TrackPieceVariant variant, float wallExtra, ref PieceGeom g)
        {
            ResolveDiagBendEndpoints(def.Shape, out var sCenter, out var sRight, out var eCenter, out var eRight);
            Vector2 sFwd = new(-sRight.y, sRight.x);
            Vector2 eFwd = new(-eRight.y, eRight.x);
            float chord = Vector2.Distance(sCenter, eCenter);
            Vector2 m0 = sFwd * (1.5f * chord);
            Vector2 m1 = eFwd * (1.5f * chord);

            int seg = CurveSegSmall;
            var samples = new Vector2[seg + 1];
            var rights = new Vector2[seg + 1];
            for (int i = 0; i <= seg; i++)
            {
                float t = i / (float)seg;
                samples[i] = Hermite(sCenter, m0, eCenter, m1, t);
                Vector2 tan = HermiteTangent(sCenter, m0, eCenter, m1, t);
                rights[i] = RightPerp(tan);
                g.Centerline.Add(samples[i]);
            }

            if (variant.Edges == null) return;
            float off = (def.Dimensions.Width * RoadHalfWidth) + WallOffset + wallExtra;
            for (int i = 0; i < variant.Edges.Count; i++)
            {
                var e = variant.Edges[i];
                int sideSign;
                if (e.Anchor == EdgeAnchor.DiagonalRight) sideSign = +1;
                else if (e.Anchor == EdgeAnchor.DiagonalLeft) sideSign = -1;
                else continue;
                var band = new List<Vector2>(seg + 1);
                for (int s = 0; s <= seg; s++)
                {
                    float t = s / (float)seg;
                    if (t < e.StartT || t > e.EndT) continue;
                    band.Add(samples[s] + rights[s] * (sideSign * off));
                }
                if (band.Count >= 2) g.Walls.Add(band);
            }
        }

        private static void ResolveDiagBendEndpoints(TrackPieceShape shape,
            out Vector2 sCenter, out Vector2 sRight, out Vector2 eCenter, out Vector2 eRight)
        {
            const float K = 0.7071067811865475f;
            if (shape == TrackPieceShapes.CurveDiagToCardinal_1x1 ||
                shape == TrackPieceShapes.CurveDiagToCardinalLeft_1x1)
            {
                sCenter = new Vector2(1f, 1f);
                sRight = new Vector2(-K, K);
                eCenter = new Vector2(1f, 0.5f);
                eRight = new Vector2(0f, -1f);
            }
            else if (shape == TrackPieceShapes.CurveDiagHairpin_1x1)
            {
                sCenter = new Vector2(1f, 1f);
                sRight = new Vector2(-K, K);
                eCenter = new Vector2(0f, 0.5f);
                eRight = new Vector2(0f, 1f);
            }
            else // CurveDiagTransition_1x1 (and Left mirror handled via MirrorX flag)
            {
                sCenter = new Vector2(0.5f, 0f);
                sRight = new Vector2(1f, 0f);
                eCenter = new Vector2(1f, 1f);
                eRight = new Vector2(K, -K);
            }
        }

        private static Vector2 Hermite(Vector2 p0, Vector2 m0, Vector2 p1, Vector2 m1, float t)
        {
            float t2 = t * t;
            float t3 = t2 * t;
            float h00 = 2f * t3 - 3f * t2 + 1f;
            float h10 = t3 - 2f * t2 + t;
            float h01 = -2f * t3 + 3f * t2;
            float h11 = t3 - t2;
            return h00 * p0 + h10 * m0 + h01 * p1 + h11 * m1;
        }

        private static Vector2 HermiteTangent(Vector2 p0, Vector2 m0, Vector2 p1, Vector2 m1, float t)
        {
            float t2 = t * t;
            float dh00 = 6f * t2 - 6f * t;
            float dh10 = 3f * t2 - 4f * t + 1f;
            float dh01 = -6f * t2 + 6f * t;
            float dh11 = 3f * t2 - 2f * t;
            return dh00 * p0 + dh10 * m0 + dh01 * p1 + dh11 * m1;
        }

        private static Vector2 RightPerp(Vector2 forward)
        {
            float mag = forward.magnitude;
            if (mag < 1e-6f) return new Vector2(1f, 0f);
            forward /= mag;
            return new Vector2(forward.y, -forward.x);
        }

        // Fallback for bezier-closure / unknown families: 3-point polyline
        // through tile-center derived from port directions. Edges not rendered
        // (these families don't carry the catalog edge anchors anyway).
        private static void BuildPortPolyline(TrackPieceDefinition def, ref PieceGeom g)
        {
            int W = def.Dimensions.Width;
            int L = def.Dimensions.Length;
            TrackDirection? d1 = null, d2 = null;
            for (int i = 0; i < def.Ports.Count; i++)
            {
                var dir = def.Ports[i].Side;
                if (d1 == null) { d1 = dir; continue; }
                if (dir == d1.Value) continue;
                d2 = dir; break;
            }
            if (d1 == null || d2 == null) return;
            Vector2 center = new(W * 0.5f, L * 0.5f);
            g.Centerline.Add(center + UnitStep(d1.Value) * 0.5f);
            g.Centerline.Add(center);
            g.Centerline.Add(center + UnitStep(d2.Value) * 0.5f);
        }

        private static void MirrorXList(List<Vector2> pts, int W)
        {
            for (int i = 0; i < pts.Count; i++)
            {
                var p = pts[i];
                pts[i] = new Vector2(W - p.x, p.y);
            }
        }

        private static void EmitArc(List<Vector2> dst, Vector2 center, float r, float a0, float a1, int seg)
        {
            for (int i = 0; i <= seg; i++)
            {
                float a = Mathf.Lerp(a0, a1, i / (float)seg);
                dst.Add(new Vector2(center.x + r * Mathf.Cos(a), center.y + r * Mathf.Sin(a)));
            }
        }

        // 8-way facing rotation. facing=0 (North) is identity. facing=k rotates
        // CW by k·45° in the xy plane (matches TrackDirection composition: a
        // canonical North port placed with facing=East ends up pointing East in
        // world). Pivot is the canonical bbox center; output is in world grid
        // coords centered on the placed-piece world center.
        private static void TransformList(List<Vector2> pts, Vector2 pivot, Vector2 worldOrigin, int facing)
        {
            float a = -facing * Mathf.PI * 0.25f;
            float c = Mathf.Cos(a), s = Mathf.Sin(a);
            for (int i = 0; i < pts.Count; i++)
            {
                Vector2 q = pts[i] - pivot;
                pts[i] = new Vector2(q.x * c - q.y * s, q.x * s + q.y * c) + worldOrigin;
            }
        }

        private static Vector2 UnitStep(TrackDirection d) => d switch
        {
            TrackDirection.North => new Vector2(0, 1),
            TrackDirection.NorthEast => new Vector2(0.7071f, 0.7071f),
            TrackDirection.East => new Vector2(1, 0),
            TrackDirection.SouthEast => new Vector2(0.7071f, -0.7071f),
            TrackDirection.South => new Vector2(0, -1),
            TrackDirection.SouthWest => new Vector2(-0.7071f, -0.7071f),
            TrackDirection.West => new Vector2(-1, 0),
            TrackDirection.NorthWest => new Vector2(-0.7071f, 0.7071f),
            _ => Vector2.zero
        };
    }
}
