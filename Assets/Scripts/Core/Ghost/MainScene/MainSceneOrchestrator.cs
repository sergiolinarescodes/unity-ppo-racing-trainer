using UnityPpoRacingTrainer.Core.Ghost.Director;
using UnityPpoRacingTrainer.Core.Terrain;
using UnityPpoRacingTrainer.Core.Track;
using UnityPpoRacingTrainer.Core.Track.Generation.StarterStrip;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Ghost.MainScene
{
    internal sealed class MainSceneOrchestrator : IMainSceneOrchestrator
    {
        // Mini-Metro / iso-3D camera. Mirrors HeuristicLapScenario.ApplyIsoCamera:
        // high oblique angle so a 16-cell strip + 8-octant orientations all read.
        private const float CamOrthoSize = 8f;
        private static readonly Vector3 CamOffset = new(10f, 14f, -10f);
        // 100×100 to match trainer-test's TerrainWidth/Depth so saved
        // authored-closure circuits (which place pieces in the 40–60 cell
        // band around the centre) fit inside the bootstrap terrain. The
        // CircuitLoaderInputService Shift+C path requires this.
        private const int TerrainSize = 100;

        private readonly ITerrainService _terrain;
        private readonly IStarterStripGenerator _stripGenerator;
        private readonly IGameSceneDirector _director;
        private bool _ran;

        public MainSceneOrchestrator(
            ITerrainService terrain,
            IStarterStripGenerator stripGenerator,
            IGameSceneDirector director)
        {
            _terrain = terrain;
            _stripGenerator = stripGenerator;
            _director = director;
        }

        public void Run()
        {
            if (_ran) return;
            _ran = true;

            EnsureTerrain();
            ConfigureOrthoCamera();

            int seed = (int)System.DateTime.UtcNow.Ticks;
            var req = new StarterStripRequest(seed, OctantOverride: null, MinPieces: 6, MaxPieces: 10);
            var result = _stripGenerator.Generate(in req);

            Debug.Log($"[MainSceneOrchestrator] starter strip success={result.Success} octant={result.Octant} pieces={result.PieceCount}");

            _director.StartGhostLoop();
        }

        private void EnsureTerrain()
        {
            if (_terrain.IsInitialized) return;
            _terrain.Initialize(new TerrainBuildOptions(TerrainSize, TerrainSize, 0, TrackPieceConstants.CellSize));
        }

        private static void ConfigureOrthoCamera()
        {
            var cam = Camera.main;
            if (cam == null)
            {
                Debug.LogWarning("[MainSceneOrchestrator] Camera.main is null — iso camera skipped.");
                return;
            }
            float c = TrackPieceConstants.CellSize;
            cam.orthographic = true;
            cam.orthographicSize = CamOrthoSize * c;
            // Frame the centre of the terrain (cells 0..N-1 → world centre at N/2 * c).
            var target = new Vector3(TerrainSize * 0.5f * c, 0f, TerrainSize * 0.5f * c);
            cam.transform.position = target + CamOffset * c;
            cam.transform.LookAt(target);
        }
    }
}
