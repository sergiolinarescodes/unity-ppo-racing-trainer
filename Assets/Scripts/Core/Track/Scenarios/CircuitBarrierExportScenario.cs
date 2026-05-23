using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityPpoRacingTrainer.Core.Terrain;
using UnityPpoRacingTrainer.Core.Terrain.Scenarios;
using UnityPpoRacingTrainer.Core.Track.Loop;
using Unidad.Core.Grid;
using Unidad.Core.Testing;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Track.Scenarios
{
    /// <summary>
    /// Re-exports every circuit JSON under <c>circuits/*/*.json</c> with the
    /// current backend's wall geometry baked in. For each file: replay every
    /// placement through the live <see cref="TrackPlacementService"/> on a flat terrain,
    /// read <see cref="TrackCollisionService.AllWalls"/>, and inject the <c>Walls</c>
    /// array into the JSON. Any pre-existing <c>Kerbs</c> array is stripped and
    /// replaced with an empty array — static kerbs were removed; kerbs are now placed
    /// dynamically by the racing-line kerb service during the ghost-loop preview.
    /// Placements that fail to replay (unknown shape, validator reject, etc.) cause
    /// the file to be quarantined into <c>_invalid/</c> — keeps "fake" circuits out
    /// of the library.
    ///
    /// Run from the Scenario Browser. Idempotent: re-running strips and rewrites
    /// the Walls + Kerbs fields, so it's safe after every barrier-rule change.
    /// </summary>
    internal sealed class CircuitBarrierExportScenario : DataDrivenScenario
    {
        private const int TerrainWidth = 80;
        private const int TerrainDepth = 80;
        private const string CircuitsRoot = "circuits";
        private const string QuarantineDir = "_invalid";

        private ScenarioEventBus _eventBus;
        private ScenarioGameObjectFactory _factory;
        private TerrainService _terrain;
        private TrackPieceCatalog _pieceCatalog;
        private TrackPieceMeshBuilder _pieceMeshBuilder;
        private List<ITrackPlacementValidator> _validators;
        private TrackCollisionService _collision;
        private TrackPlacementService _placement;
        private ClosedLoopService _loop;

        private int _processed;
        private int _written;
        private int _quarantined;

        public CircuitBarrierExportScenario() : base(new TestScenarioDefinition(
            "circuit-barrier-export",
            "Circuit Barrier Re-Export",
            "Walks circuits/*/*.json, replays each via TrackPlacementService, and " +
            "bakes the current backend's Walls + Kerbs into the JSON. Quarantines circuits " +
            "that no longer replay (fakes). Run after any AutoBarriers / piece-definition change.",
            Array.Empty<ScenarioParameter>()))
        { }

        protected override void ExecuteInternal(ScenarioParameterOverrides overrides)
        {
            _eventBus = new ScenarioEventBus();
            _factory = new ScenarioGameObjectFactory();
            BuildServices();

            string root = ResolveCircuitsRoot();
            if (root == null || !Directory.Exists(root))
            {
                Debug.LogError($"[CircuitBarrierExport] circuits root not found (cwd={Directory.GetCurrentDirectory()}). Run Unity from the repo root.");
                return;
            }

            _processed = 0;
            _written = 0;
            _quarantined = 0;

            foreach (var subDir in Directory.GetDirectories(root))
            {
                foreach (var jsonPath in Directory.GetFiles(subDir, "*.json"))
                    Process(jsonPath, subDir);
            }

            Debug.Log($"[CircuitBarrierExport] root={root} processed={_processed} " +
                      $"written={_written} quarantined={_quarantined}");
        }

        private void BuildServices()
        {
            _terrain = new TerrainService(_eventBus);
            _terrain.Initialize(new TerrainBuildOptions(TerrainWidth, TerrainDepth, 0, TrackPieceConstants.CellSize));
            int cw = _terrain.CornerWidth;
            int cd = _terrain.CornerDepth;
            var heights = new float[cw, cd];
            _terrain.TrySetAllCorners(heights);

            _pieceCatalog = new TrackPieceCatalog();
            TrackPieceCatalogSeeder.Seed(_pieceCatalog);
            _pieceMeshBuilder = new TrackPieceMeshBuilder(new FlatHeightAdapter());

            _validators = new List<ITrackPlacementValidator>
            {
                new BoundsValidator(),
                new OverlapValidator(),
                new TerrainCompatibilityValidator()
            };

            _collision = new TrackCollisionService();
            _placement = new TrackPlacementService(
                _eventBus, _pieceCatalog, _pieceMeshBuilder,
                _validators, _terrain, _factory, TrackPalette.Default,
                _collision);
            // ClosedLoopService listens on the placement event bus and rebuilds
            // the canonical ClosedLoop (with CCW-flipped Anchors +
            // LapStartAnchorIndex + Sectors.MicroBoundaryAnchor) every time we
            // call _placement.TryPlace. We use it as the SOLE source of truth
            // for the anchor/lap-start metadata baked into each JSON — the
            // Python dashboard reads those fields directly and never falls
            // back to its own heuristic.
            _loop = new ClosedLoopService(_eventBus, _placement, _pieceCatalog, _terrain);
        }

        private static string ResolveCircuitsRoot()
        {
            // Editor cwd is usually the project root, where circuits/ lives.
            string cwd = Directory.GetCurrentDirectory();
            string p = Path.Combine(cwd, CircuitsRoot);
            if (Directory.Exists(p)) return p;
            // Fallback: walk up from cwd looking for circuits/.
            DirectoryInfo d = new(cwd);
            for (int i = 0; i < 6 && d != null; i++, d = d.Parent)
            {
                string c = Path.Combine(d.FullName, CircuitsRoot);
                if (Directory.Exists(c)) return c;
            }
            return null;
        }

        private void Process(string jsonPath, string subDir)
        {
            _processed++;
            string text;
            try { text = File.ReadAllText(jsonPath); }
            catch (Exception e)
            {
                Debug.LogWarning($"[CircuitBarrierExport] read fail {jsonPath}: {e.Message}");
                return;
            }

            CircuitFile rec;
            try { rec = JsonUtility.FromJson<CircuitFile>(text); }
            catch (Exception e)
            {
                Debug.LogWarning($"[CircuitBarrierExport] parse fail {jsonPath}: {e.Message}");
                Quarantine(jsonPath, subDir, "parse-failed");
                return;
            }
            if (rec == null || rec.Placements == null || rec.Placements.Count == 0)
            {
                Quarantine(jsonPath, subDir, "empty-or-null");
                return;
            }

            _placement.Clear();
            for (int i = 0; i < rec.Placements.Count; i++)
            {
                var p = rec.Placements[i];
                var r = _placement.TryPlace(
                    new TrackPieceShape(p.ShapeId),
                    new GridPosition(p.X, p.Y),
                    (TrackDirection)p.Facing);
                if (!r.Success)
                {
                    Debug.LogWarning($"[CircuitBarrierExport] replay fail {Path.GetFileName(jsonPath)} piece {i} ({p.ShapeId}@{p.X},{p.Y},{p.Facing}): {r.Reason}");
                    Quarantine(jsonPath, subDir, $"replay-fail-piece-{i}");
                    return;
                }
            }

            if (!_loop.TryGetCurrentLoop(out var closedLoop))
            {
                Debug.LogWarning($"[CircuitBarrierExport] loop not closed after replay {Path.GetFileName(jsonPath)}");
                Quarantine(jsonPath, subDir, "loop-not-closed");
                return;
            }

            string wallsJson = SerializeWalls(_collision.AllWalls);
            // Static kerbs were removed. Always emit empty array so downstream
            // readers can rely on the field's presence.
            const string kerbsJson = "[]";
            string anchorsJson = SerializeAnchors(closedLoop.Anchors);
            string microJson = SerializeIntArray(closedLoop.Sectors.MicroBoundaryAnchor);
            int lapStartIdx = closedLoop.LapStartAnchorIndex;
            int anchorCount = closedLoop.Anchors?.Count ?? 0;
            float totalLength = closedLoop.TotalLength;

            // Strip and re-inject so re-running the scenario picks up any
            // backend changes (anchor re-flip, lap-start migration, new
            // sector count). All canonical fields are owned by ClosedLoop —
            // the Python dashboard treats them as authoritative.
            string stripped = text;
            stripped = StripField(stripped, "Walls");
            stripped = StripField(stripped, "Kerbs");
            stripped = StripField(stripped, "Anchors");
            stripped = StripField(stripped, "AnchorCount");
            stripped = StripField(stripped, "TotalLength");
            stripped = StripField(stripped, "LapStartAnchorIndex");
            stripped = StripField(stripped, "MicroBoundaryAnchor");
            string fragment =
                $"\"TotalLength\":{totalLength.ToString("0.###", CultureInfo.InvariantCulture)}" +
                $",\"AnchorCount\":{anchorCount}" +
                $",\"Anchors\":{anchorsJson}" +
                $",\"LapStartAnchorIndex\":{lapStartIdx}" +
                $",\"MicroBoundaryAnchor\":{microJson}" +
                $",\"Walls\":{wallsJson}" +
                $",\"Kerbs\":{kerbsJson}";
            string output = InjectBeforeClose(stripped, fragment);

            try
            {
                File.WriteAllText(jsonPath, output);
                _written++;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CircuitBarrierExport] write fail {jsonPath}: {e.Message}");
            }
        }

        // Anchors live in world XZ; the dashboard expects pairs in the legacy
        // [x, y] format (where "y" is world Z). The legacy-anchor-CellSize=2
        // rescale in server.py.load_circuits intentionally leaves cellSize=3
        // anchors at 1.0 scale, so emit raw world coordinates.
        private static string SerializeAnchors(System.Collections.Generic.IReadOnlyList<UnityPpoRacingTrainer.Core.Track.Ribbon.TrackChainAnchor> anchors)
        {
            var sb = new StringBuilder();
            sb.Append('[');
            if (anchors != null)
            {
                for (int i = 0; i < anchors.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    var a = anchors[i].WorldPos;
                    sb.Append('[');
                    AppendF(sb, a.x); sb.Append(',');
                    AppendF(sb, a.z);
                    sb.Append(']');
                }
            }
            sb.Append(']');
            return sb.ToString();
        }

        private static string SerializeIntArray(System.Collections.Generic.IReadOnlyList<int> values)
        {
            var sb = new StringBuilder();
            sb.Append('[');
            if (values != null)
            {
                for (int i = 0; i < values.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(values[i].ToString(CultureInfo.InvariantCulture));
                }
            }
            sb.Append(']');
            return sb.ToString();
        }

        private void Quarantine(string jsonPath, string subDir, string reason)
        {
            try
            {
                string qDir = Path.Combine(subDir, QuarantineDir);
                Directory.CreateDirectory(qDir);
                string dest = Path.Combine(qDir, Path.GetFileName(jsonPath));
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(jsonPath, dest);
                _quarantined++;
                Debug.LogWarning($"[CircuitBarrierExport] quarantined {Path.GetFileName(jsonPath)} → {qDir} ({reason})");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CircuitBarrierExport] quarantine fail {jsonPath}: {e.Message}");
            }
        }

        private static string SerializeWalls(IReadOnlyList<WallSegment> walls)
        {
            var sb = new StringBuilder();
            sb.Append('[');
            for (int i = 0; i < walls.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var w = walls[i];
                sb.Append('[');
                AppendF(sb, w.A.x); sb.Append(',');
                AppendF(sb, w.A.y); sb.Append(',');
                AppendF(sb, w.B.x); sb.Append(',');
                AppendF(sb, w.B.y);
                sb.Append(']');
            }
            sb.Append(']');
            return sb.ToString();
        }

        private static void AppendF(StringBuilder sb, float v)
            => sb.Append(v.ToString("0.###", CultureInfo.InvariantCulture));

        // Removes "<field>":<value> from the JSON text, including the leading comma.
        // Walks bracket depth so nested arrays/objects are handled. Idempotent.
        private static string StripField(string json, string fieldName)
        {
            string key = "\"" + fieldName + "\":";
            int idx = json.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0) return json;

            int start = idx;
            while (start > 0 && char.IsWhiteSpace(json[start - 1])) start--;
            if (start > 0 && json[start - 1] == ',') start--;

            int p = idx + key.Length;
            while (p < json.Length && char.IsWhiteSpace(json[p])) p++;
            int end;
            char open = json[p];
            if (open == '[' || open == '{')
            {
                char close = open == '[' ? ']' : '}';
                int depth = 0;
                for (end = p; end < json.Length; end++)
                {
                    char c = json[end];
                    if (c == '"') { end = SkipString(json, end); continue; }
                    if (c == open) depth++;
                    else if (c == close) { depth--; if (depth == 0) { end++; break; } }
                }
            }
            else if (open == '"')
            {
                end = SkipString(json, p) + 1;
            }
            else
            {
                end = p;
                while (end < json.Length && ",}]".IndexOf(json[end]) < 0) end++;
            }
            return json.Substring(0, start) + json.Substring(end);
        }

        private static int SkipString(string json, int p)
        {
            // p points at the opening quote; returns index of the closing quote.
            for (int i = p + 1; i < json.Length; i++)
            {
                if (json[i] == '\\') { i++; continue; }
                if (json[i] == '"') return i;
            }
            return json.Length - 1;
        }

        private static string InjectBeforeClose(string json, string fragment)
        {
            int close = json.LastIndexOf('}');
            if (close < 0) return json;
            string head = json.Substring(0, close).TrimEnd();
            string tail = json.Substring(close);
            string sep = head.EndsWith("{", StringComparison.Ordinal) ? "" : ",";
            return head + sep + fragment + tail;
        }

        protected override ScenarioVerificationResult VerifyInternal(ScenarioParameterOverrides overrides)
        {
            var checks = new List<ScenarioVerificationResult.CheckResult>
            {
                new("services-built", _placement != null && _collision != null, "placement+collision built"),
                new("processed-non-zero", _processed > 0, $"processed={_processed}"),
            };
            return new ScenarioVerificationResult(checks);
        }

        protected override void OnCleanup()
        {
            _loop?.Dispose();
            _placement?.Dispose();
            _factory?.Dispose();
            _eventBus?.ClearAllSubscriptions();
            base.OnCleanup();
        }

        [Serializable]
        private sealed class CircuitFile
        {
            public string Id;
            public float TotalLength;
            public int AnchorCount;
            public List<Placement> Placements;

            [Serializable]
            public sealed class Placement
            {
                public string ShapeId;
                public int X;
                public int Y;
                public int Facing;
            }
        }
    }
}
