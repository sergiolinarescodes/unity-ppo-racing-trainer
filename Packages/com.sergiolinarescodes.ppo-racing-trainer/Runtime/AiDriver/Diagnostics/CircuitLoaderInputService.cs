using System;
using System.Collections.Generic;
using System.IO;
using UnityPpoRacingTrainer.Core.Track;
using Reflex.Core;
using Unidad.Core.Abstractions;
using Unidad.Core.Bootstrap;
using Unidad.Core.EventBus;
using Unidad.Core.Grid;
using Unidad.Core.Testing;
using UnityEngine;
using UnityEngine.InputSystem;

namespace UnityPpoRacingTrainer.Core.AiDriver.Diagnostics
{
    /// <summary>
    /// Diagnostic loader — on Shift+C, replays a saved authored-closure
    /// circuit JSON into the live placement service so the bootstrap scene
    /// instantly contains the same closed loop the trainer test runs on.
    /// Use this to side-by-side compare ML agent behaviour between scenes
    /// without rebuilding the track by hand.
    ///
    /// Circuit JSON lives at <c>circuits/stage_authored_closure/circuit_*.json</c>
    /// relative to the project root. Schema mirrors
    /// <c>AuthoredClosureCircuitLibrary.CircuitFile</c> (a small subset is
    /// duplicated here so this service has no Scenario-side dependency).
    /// </summary>
    internal sealed class CircuitLoaderInputService : ITickable
    {
        // Hard-coded match for the trainer-test circuit the user wants to
        // mirror. Change the constant to load a different saved circuit.
        private const string CircuitJsonRelativePath =
            "circuits/stage_authored_closure/circuit_1408150785.json";

        private readonly ITrackPlacementService _placement;

        public CircuitLoaderInputService(ITrackPlacementService placement)
        {
            _placement = placement;
        }

        public void Tick(float deltaTime)
        {
            var kb = Keyboard.current;
            if (kb == null) return;
            bool shift = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;
            if (!shift || !kb.cKey.wasPressedThisFrame) return;
            LoadCircuit();
        }

        private void LoadCircuit()
        {
            // Application.dataPath = <project>/Assets. Walk up one level to
            // reach the project root where the circuits/ folder lives.
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string fullPath = Path.Combine(root, CircuitJsonRelativePath);
            if (!File.Exists(fullPath))
            {
                Debug.LogError($"[CircuitLoader] file missing: {fullPath}");
                return;
            }

            CircuitFileDto file;
            try
            {
                file = JsonUtility.FromJson<CircuitFileDto>(File.ReadAllText(fullPath));
            }
            catch (Exception e)
            {
                Debug.LogError($"[CircuitLoader] parse failed: {e.Message}");
                return;
            }
            if (file?.Placements == null || file.Placements.Count == 0)
            {
                Debug.LogError("[CircuitLoader] empty Placements list — nothing to load.");
                return;
            }

            _placement.Clear();
            int placed = 0;
            int failed = 0;
            int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
            for (int i = 0; i < file.Placements.Count; i++)
            {
                var p = file.Placements[i];
                var r = _placement.TryPlace(
                    new TrackPieceShape(p.ShapeId),
                    new GridPosition(p.X, p.Y),
                    (TrackDirection)p.Facing,
                    TrackPieceVariantId.Default,
                    animate: false);
                if (r.Success)
                {
                    placed++;
                    if (p.X < minX) minX = p.X;
                    if (p.X > maxX) maxX = p.X;
                    if (p.Y < minY) minY = p.Y;
                    if (p.Y > maxY) maxY = p.Y;
                }
                else
                {
                    failed++;
                    Debug.LogWarning($"[CircuitLoader] piece {i} ({p.ShapeId}@{p.X},{p.Y} facing={p.Facing}) failed: {r.Reason}");
                }
            }
            Debug.Log($"[CircuitLoader] loaded {file.Id} placed={placed} failed={failed} of {file.Placements.Count}");
            if (placed > 0) FrameCircuit(minX, maxX, minY, maxY);
        }

        // Reframes Camera.main on the loaded circuit's cell bbox. Bootstrap's
        // MainSceneOrchestrator configures the camera once at Run() based on
        // the small 32×32 starter strip, so after a Shift+C load the saved
        // 100-cell circuit lies entirely outside the view frustum.
        private static void FrameCircuit(int minX, int maxX, int minY, int maxY)
        {
            var cam = Camera.main;
            if (cam == null) return;
            float c = TrackPieceConstants.CellSize;
            // +1 cell padding around the bbox; pieces extend beyond their
            // origin cell so the raw bbox understates the visual footprint.
            float spanCellsX = (maxX - minX) + 2f;
            float spanCellsZ = (maxY - minY) + 2f;
            float centerX = (minX + maxX + 1) * 0.5f * c;
            float centerZ = (minY + maxY + 1) * 0.5f * c;
            var target = new Vector3(centerX, 0f, centerZ);

            // Ortho size = half the larger axis in world units, with ~30%
            // margin so the loop doesn't kiss the screen edge.
            float halfSpanWorld = Mathf.Max(spanCellsX, spanCellsZ) * c * 0.5f;
            float orthoSize = halfSpanWorld * 1.3f;
            cam.orthographicSize = orthoSize;
            // Pull the camera up + back proportional to the new ortho size
            // so the iso angle is preserved on bigger loops.
            var offset = new Vector3(orthoSize * 0.8f, orthoSize * 1.0f, -orthoSize * 0.8f);
            cam.transform.position = target + offset;
            cam.transform.LookAt(target);
            Debug.Log($"[CircuitLoader] camera reframed → center=({centerX:0.0},{centerZ:0.0}) ortho={orthoSize:0.0}");
        }

        // Subset of AuthoredClosureCircuitLibrary.CircuitFile — only the
        // fields needed to replay placements. JsonUtility silently ignores
        // unknown fields so the rest of the JSON (anchors, sectors, walls)
        // is dropped on the floor as intended.
        [Serializable]
        private sealed class CircuitFileDto
        {
            public string Id;
            public List<PlacementDto> Placements;
        }

        [Serializable]
        private sealed class PlacementDto
        {
            public string ShapeId;
            public int X;
            public int Y;
            public int Facing;
        }
    }

    public sealed class CircuitLoaderSystemInstaller : ISystemInstaller
    {
        public void Install(ContainerBuilder builder)
        {
            builder.AddSingleton(c => new CircuitLoaderInputService(
                    c.Resolve<ITrackPlacementService>()),
                typeof(CircuitLoaderInputService),
                typeof(ITickable));
        }

        public ISystemTestFactory CreateTestFactory() => new CircuitLoaderTestFactory();
    }

    internal sealed class CircuitLoaderTestFactory : ISystemTestFactory
    {
        public Type[] TestedServices => new[] { typeof(CircuitLoaderInputService) };
        public object CreateForTesting(TestDependencies deps) => null;
        public IEnumerable<ITestScenario> GetScenarios() { yield break; }
    }
}
