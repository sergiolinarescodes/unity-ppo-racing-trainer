using System;
using UnityPpoRacingTrainer.Core.Terrain.Generators;
using Unidad.Core.Abstractions;
using Unidad.Core.EventBus;
using Unidad.Core.Factory;
using Unidad.Core.Systems;
using UnityEngine;
using UnityEngine.InputSystem;

namespace UnityPpoRacingTrainer.Core.Terrain.Showcase
{
    internal sealed class TerrainShowcaseService : SystemServiceBase, ITerrainShowcaseService, ITickable
    {
        private readonly ITerrainService _terrain;
        private readonly ITerrainMeshBuilder _meshBuilder;
        private readonly IGameObjectFactory _factory;

        private const int Width = 48;
        private const int Depth = 48;
        private const int Seed = 1337;
        private const int MaxLevel = 6;
        private const float OrbitRadius = 60f;
        private const float OrbitHeight = 38f;
        private const float OrbitSpeedDeg = 12f;
        private const float ModeCycleSeconds = 12f;

        private static readonly TerrainGeneratorMode[] ModeOrder =
        {
            TerrainGeneratorMode.Plains,
            TerrainGeneratorMode.GentleSlope,
            TerrainGeneratorMode.CenterPit,
            TerrainGeneratorMode.CenterMound,
            TerrainGeneratorMode.PerimeterRing,
            TerrainGeneratorMode.TerracedRows,
            TerrainGeneratorMode.Mountainous
        };

        private GameObject _meshObject;
        private Mesh _mesh;
        private Material _material;
        private Vector3 _terrainCenter;
        private float _orbitAngle;
        private float _modeTimer;
        private int _modeIndex;
        private int _cycleCount;
        private TerrainColorMode _colorMode = TerrainColorMode.Palette;
        private bool _running;

        private Camera _camera;
        private Vector3 _origCamPos;
        private Quaternion _origCamRot;
        private bool _camCaptured;

        public TerrainShowcaseService(
            IEventBus eventBus,
            ITerrainService terrain,
            ITerrainMeshBuilder meshBuilder,
            IGameObjectFactory factory) : base(eventBus)
        {
            _terrain = terrain;
            _meshBuilder = meshBuilder;
            _factory = factory;
        }

        public bool IsRunning => _running;

        public void Start()
        {
            if (_running) return;
            _terrain.Initialize(new TerrainBuildOptions(Width, Depth, 0, Track.TrackPieceConstants.CellSize));

            _meshObject = _factory.CreateEmpty("[Showcase] TerrainMesh");
            _meshObject.transform.position = new Vector3(-Width * 0.5f, 0f, -Depth * 0.5f);

            var mf = _meshObject.AddComponent<MeshFilter>();
            var mr = _meshObject.AddComponent<MeshRenderer>();
            var shader = Shader.Find("RaceConstructor/TerrainVertexColor")
                         ?? Shader.Find("Universal Render Pipeline/Lit");
            _material = new Material(shader);
            _material.color = Color.white;
            mr.sharedMaterial = _material;

            _modeIndex = 0;
            _modeTimer = 0f;
            ApplyMode(ModeOrder[_modeIndex]);
            _mesh = _meshBuilder.Build(_terrain, TerrainPalette.BoneAndSlate, _colorMode);
            mf.sharedMesh = _mesh;

            _camera = Camera.main;
            if (_camera != null)
            {
                _origCamPos = _camera.transform.position;
                _origCamRot = _camera.transform.rotation;
                _camCaptured = true;
            }

            _terrainCenter = new Vector3(0f, _terrain.WorldBounds.center.y, 0f);
            _orbitAngle = 0f;
            _running = true;
            UpdateCamera(0f);
        }

        public void Stop()
        {
            if (!_running) return;
            if (_meshObject != null) _factory.Destroy(_meshObject);
            if (_mesh != null) UnityEngine.Object.Destroy(_mesh);
            if (_material != null) UnityEngine.Object.Destroy(_material);
            _meshObject = null;
            _mesh = null;
            _material = null;
            if (_camCaptured && _camera != null)
            {
                _camera.transform.position = _origCamPos;
                _camera.transform.rotation = _origCamRot;
            }
            _camCaptured = false;
            _running = false;
        }

        public void Tick(float deltaTime)
        {
            if (!_running) return;

            HandleInput();

            _modeTimer += deltaTime;
            if (_modeTimer >= ModeCycleSeconds)
            {
                _modeTimer = 0f;
                AdvanceMode();
            }

            UpdateCamera(deltaTime);
        }

        // ---- internals ----

        private void HandleInput()
        {
            var kbd = Keyboard.current;
            if (kbd == null) return;
            if (kbd.tabKey.wasPressedThisFrame)
            {
                _colorMode = _colorMode == TerrainColorMode.Palette
                    ? TerrainColorMode.Categories
                    : TerrainColorMode.Palette;
                _meshBuilder.Rebuild(_mesh, _terrain, TerrainPalette.BoneAndSlate, _colorMode);
                Debug.Log($"[TerrainShowcase] Color mode: {_colorMode}");
            }
            if (kbd.spaceKey.wasPressedThisFrame)
            {
                _modeTimer = 0f;
                AdvanceMode();
            }
        }

        private void AdvanceMode()
        {
            _modeIndex = (_modeIndex + 1) % ModeOrder.Length;
            _cycleCount++;
            ApplyMode(ModeOrder[_modeIndex]);
            _meshBuilder.Rebuild(_mesh, _terrain, TerrainPalette.BoneAndSlate, _colorMode);
            _terrainCenter = new Vector3(0f, _terrain.WorldBounds.center.y, 0f);
        }

        private void ApplyMode(TerrainGeneratorMode mode)
        {
            int cw = _terrain.CornerWidth;
            int cd = _terrain.CornerDepth;
            var levels = new int[cw, cd];
            int seed = Seed + (int)mode * 7919 + _cycleCount * 31;
            TerrainGenerators.Fill(mode, levels, seed, MaxLevel);

            var heights = new float[cw, cd];
            for (int z = 0; z < cd; z++)
                for (int x = 0; x < cw; x++)
                    heights[x, z] = TerrainShapeRules.ToHeight(levels[x, z]);
            bool ok = _terrain.TrySetAllCorners(heights);

            int flat = 0, ramp = 0, angle = 0;
            foreach (var pos in _terrain.AllTiles)
            {
                switch (_terrain.GetTile(pos).Shape.GetCategory())
                {
                    case TerrainShapeCategory.Flat: flat++; break;
                    case TerrainShapeCategory.CardinalRamp: ramp++; break;
                    case TerrainShapeCategory.AngleSlope: angle++; break;
                }
            }
            Debug.Log($"[TerrainShowcase] {mode}: flat={flat} ramps={ramp} angles={angle} applied={ok}");
        }

        private void UpdateCamera(float dt)
        {
            if (_camera == null) return;
            _orbitAngle += OrbitSpeedDeg * dt;
            float rad = _orbitAngle * Mathf.Deg2Rad;
            var pos = new Vector3(Mathf.Cos(rad) * OrbitRadius, OrbitHeight, Mathf.Sin(rad) * OrbitRadius);
            _camera.transform.position = pos + _terrainCenter;
            _camera.transform.LookAt(_terrainCenter);
        }
    }
}
