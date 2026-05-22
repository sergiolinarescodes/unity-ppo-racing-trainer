using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.AiDriver;
using UnityPpoRacingTrainer.Core.AiDriver.Loop;
using UnityPpoRacingTrainer.Core.Ghost.Presentation;
using UnityPpoRacingTrainer.Core.Ghost.Simulation;
using UnityPpoRacingTrainer.Core.Track;
using UnityPpoRacingTrainer.Core.Track.Topology;
using Unidad.Core.Abstractions;
using Unidad.Core.EventBus;
using Unidad.Core.Factory;
using Unidad.Core.Systems;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Ghost.Kerbs
{
    // -------------------------------------------------------------------------
    // Dynamic racing-line kerbs. Co-located in one file: interfaces, events,
    // recorder, and main service. Static kerbs were removed; this subsystem
    // replaces them with kerbs placed at runtime based on where the ghost car
    // actually drifts toward the edge of the road. Kerbs drop from the air via
    // the shared drop animator, then register as IKerbZone with the collision
    // service so the ghost gets the kerb-grade grip boost on subsequent laps.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Captures the ghost's per-tick world position into a buffered polyline.
    /// On every <see cref="GhostLapCompletedEvent"/> it snapshots the buffer as
    /// the "last completed lap" and resets the live buffer. On every
    /// <see cref="TrackTopologyChangedEvent"/> both buffers clear.
    /// </summary>
    public interface IRacingLineRecorder
    {
        IReadOnlyList<Vector3> LastCompletedLap { get; }
        int LastCompletedLapCount { get; }
        int LiveSampleCount { get; }
    }

    /// <summary>
    /// Owns the live set of dynamic kerbs. Listens to
    /// <see cref="GhostLapCompletedEvent"/> to analyze the racing line and emit
    /// kerb candidates, and to <see cref="TrackTopologyChangedEvent"/> to clear
    /// existing kerbs when the track changes.
    /// </summary>
    public interface IRacingLineKerbService
    {
        int ActiveKerbCount { get; }
        IReadOnlyList<DynamicKerbInstance> ActiveKerbs { get; }

        /// <summary>Force-clears all dynamic kerbs (test hook).</summary>
        void Clear();
    }

    /// <summary>
    /// One placed dynamic kerb: the synthetic id used to register/unregister it
    /// with the collision service, its parent GameObject, and the active drop
    /// handle while the animation runs.
    /// </summary>
    public readonly struct DynamicKerbInstance
    {
        public readonly TrackPieceId Id;
        public readonly GameObject GameObject;
        public readonly int Side; // -1 left, +1 right relative to centerline tangent
        public DynamicKerbInstance(TrackPieceId id, GameObject go, int side)
        {
            Id = id;
            GameObject = go;
            Side = side;
        }
    }

    /// <summary>Fired when a dynamic kerb is spawned (the prefab starts its drop).</summary>
    public readonly record struct DynamicKerbSpawnedEvent(TrackPieceId Id, Vector3 WorldPos, int Side);

    /// <summary>Fired when the dynamic kerb set is wiped — usually after a topology change.</summary>
    public readonly record struct DynamicKerbsClearedEvent(string Reason);

    internal sealed class RacingLineRecorder
        : SystemServiceBase, IRacingLineRecorder, ITickable
    {
        // Sample only every Nth tick to avoid storing thousands of near-identical points.
        // At ~60fps Tick this yields 6 samples / second which is plenty for kerb edge
        // detection while keeping the per-lap polyline small.
        private const int SampleStride = 10;

        // Cap on the live buffer so a stalled ghost (or a long open-strip drive)
        // can't grow memory without bound.
        private const int MaxLiveSamples = 4096;

        private readonly IGhostDriverService _ghost;
        private readonly List<Vector3> _live = new();
        private readonly List<Vector3> _lastLap = new();
        private int _tickCounter;

        public RacingLineRecorder(IEventBus eventBus, IGhostDriverService ghost) : base(eventBus)
        {
            _ghost = ghost;
            Subscribe<GhostLapCompletedEvent>(OnLapCompleted);
            Subscribe<TrackTopologyChangedEvent>(OnTopologyChanged);
        }

        public IReadOnlyList<Vector3> LastCompletedLap => _lastLap;
        public int LastCompletedLapCount => _lastLap.Count;
        public int LiveSampleCount => _live.Count;

        public void Tick(float deltaTime)
        {
            _tickCounter++;
            if (_tickCounter < SampleStride) return;
            _tickCounter = 0;

            if (!_ghost.HasSpawned) return;
            if (!_ghost.TryReadSnapshot(out var snap)) return;
            if (snap.IsOffTrack) return; // skip recovery excursions
            if (_live.Count >= MaxLiveSamples) return;

            _live.Add(snap.Position);
        }

        private void OnLapCompleted(GhostLapCompletedEvent _)
        {
            _lastLap.Clear();
            _lastLap.AddRange(_live);
            _live.Clear();
        }

        private void OnTopologyChanged(TrackTopologyChangedEvent _)
        {
            _live.Clear();
            _lastLap.Clear();
        }
    }

    internal sealed class RacingLineKerbService
        : SystemServiceBase, IRacingLineKerbService
    {
        // Sample is "near the edge" when |signed lateral offset| / halfwidth ≥ this.
        // 0.7 = sitting on the painted line at the road edge. Tuned so a normal
        // racing-line apex registers and a straight-line drive doesn't.
        private const float EdgeProximityThreshold = 0.70f;

        // Minimum number of consecutive near-edge samples on the same side before
        // we emit a kerb segment. Filters out one-off drift blips.
        private const int MinRunSamples = 3;

        // World-space length of a single kerb quad along the road tangent. Multiple
        // kerbs are tiled along a long run.
        private const float KerbChordLength = 0.4f;

        // Cross-track width of a kerb quad in world units. Measured from road edge
        // outward into the offside.
        private const float KerbWidthOutward = 0.18f;

        // Tiny inward overlap so the kerb visually straddles the painted edge.
        private const float KerbInwardInset = 0.04f;

        // Vertical drop height for the kerb spawn animation.
        private const float KerbDropHeight = 4f;

        // Resources paths for the kerb prefabs. Reuse the same prefabs the old
        // static system used; the dynamic system just spawns them in different
        // places (and via the racing-line analysis instead of the piece catalog).
        private const string KerbRedResourcePath = "Track/KerbRed";
        private const string KerbWhiteResourcePath = "Track/KerbWhite";

        private readonly IRacingLineRecorder _recorder;
        private readonly ITrackQueryService _trackQuery;
        private readonly ITrackCollisionService _collision;
        private readonly IGameObjectFactory _factory;
        private readonly IDropFromAirAnimator _drop;

        private readonly List<DynamicKerbInstance> _active = new();
        private GameObject _root;

        public RacingLineKerbService(
            IEventBus eventBus,
            IRacingLineRecorder recorder,
            ITrackQueryService trackQuery,
            ITrackCollisionService collision,
            IGameObjectFactory factory,
            IDropFromAirAnimator drop) : base(eventBus)
        {
            _recorder = recorder;
            _trackQuery = trackQuery;
            _collision = collision;
            _factory = factory;
            _drop = drop;

            Subscribe<GhostLapCompletedEvent>(OnLapCompleted);
            Subscribe<TrackTopologyChangedEvent>(OnTopologyChanged);
        }

        public int ActiveKerbCount => _active.Count;
        public IReadOnlyList<DynamicKerbInstance> ActiveKerbs => _active;

        public void Clear()
        {
            ClearInternal("manual");
        }

        private void OnLapCompleted(GhostLapCompletedEvent _)
        {
            if (!_trackQuery.HasLoop) return;
            if (_recorder.LastCompletedLapCount < MinRunSamples) return;

            // Topology stable + lap complete → rebuild kerbs from the line that
            // was just driven. Wipe the old set first.
            ClearInternal("relap");
            EmitKerbsFromLine(_recorder.LastCompletedLap);
        }

        private void OnTopologyChanged(TrackTopologyChangedEvent _)
        {
            ClearInternal("topology-changed");
        }

        private void EmitKerbsFromLine(IReadOnlyList<Vector3> line)
        {
            EnsureRoot();

            // Walk the polyline, run-length-encoding consecutive near-edge samples
            // on the same side. Each finalized run becomes one or more tiled kerb
            // quads along that segment of the racing line.
            int runStart = -1;
            int runSide = 0;
            for (int i = 0; i < line.Count; i++)
            {
                var proj = _trackQuery.Project(line[i]);
                if (proj.HalfWidth <= 0f) continue;
                float frac = proj.SignedLateralOffset / proj.HalfWidth;
                int side = frac > 0f ? +1 : -1;

                if (Mathf.Abs(frac) >= EdgeProximityThreshold)
                {
                    if (runStart < 0)
                    {
                        runStart = i;
                        runSide = side;
                    }
                    else if (side != runSide)
                    {
                        EmitRun(line, runStart, i - 1, runSide);
                        runStart = i;
                        runSide = side;
                    }
                }
                else
                {
                    if (runStart >= 0)
                    {
                        EmitRun(line, runStart, i - 1, runSide);
                        runStart = -1;
                    }
                }
            }
            if (runStart >= 0)
                EmitRun(line, runStart, line.Count - 1, runSide);
        }

        private void EmitRun(IReadOnlyList<Vector3> line, int startIdx, int endIdx, int side)
        {
            int n = endIdx - startIdx + 1;
            if (n < MinRunSamples) return;

            // For each sample in the run, project to centerline → offset outward
            // by halfWidth + kerb geometry → spawn one kerb quad. Adjacent samples
            // tile naturally since the polyline is dense.
            int placed = 0;
            for (int i = startIdx; i <= endIdx; i++)
            {
                if (placed > 0 && i % Mathf.Max(1, Mathf.RoundToInt(KerbChordLength * 10f)) != 0)
                    continue;

                var proj = _trackQuery.Project(line[i]);
                if (proj.HalfWidth <= 0f) continue;

                Vector3 tan = proj.Tangent;
                Vector3 right = new(tan.z, 0f, -tan.x); // +X cross-track to the right
                Vector3 outward = right * side;
                Vector3 center = proj.ProjectedPoint + outward * (proj.HalfWidth - KerbInwardInset + KerbWidthOutward * 0.5f);

                SpawnKerb(center, tan, outward, side, placed);
                placed++;
            }
        }

        private void SpawnKerb(Vector3 center, Vector3 tangent, Vector3 outward, int side, int sequenceIndex)
        {
            string path = (sequenceIndex & 1) == 0 ? KerbRedResourcePath : KerbWhiteResourcePath;
            var go = _factory.InstantiatePrefab(path, $"DynKerb_{_active.Count}", Vector3.zero);
            if (go == null) return;

            go.transform.SetParent(_root.transform, worldPositionStays: false);
            go.transform.position = center;
            // Orient prefab so local +X aligns with the tangent (matches the old
            // static kerb prefab convention).
            go.transform.rotation = Quaternion.LookRotation(new Vector3(tangent.x, 0f, tangent.z), Vector3.up);
            go.transform.localScale = new Vector3(KerbWidthOutward, 1f, KerbChordLength);

            var id = TrackPieceId.New();
            _active.Add(new DynamicKerbInstance(id, go, side));

            // Build the IKerbZone covering the kerb quad in world XZ + register so
            // the car gets the kerb-grade grip boost while its projection sits on
            // top of this kerb.
            Vector3 half = tangent * (KerbChordLength * 0.5f);
            Vector3 acrossHalf = outward * (KerbWidthOutward * 0.5f);
            Vector2 p0 = ToXZ(center - half - acrossHalf);
            Vector2 p1 = ToXZ(center + half - acrossHalf);
            Vector2 p2 = ToXZ(center + half + acrossHalf);
            Vector2 p3 = ToXZ(center - half + acrossHalf);
            var zone = new QuadKerbZone(p0, p1, p2, p3, id);
            _collision.Register(id, System.Array.Empty<WallSegment>(),
                new List<IKerbZone> { zone });

            // Kerbs use the shared drop-from-air animator. We don't track the
            // handle ourselves — kerbs are static once dropped, and the visual
            // drop is a one-shot ease. Animator state lives on the GameObject's
            // transform via a small Y-offset every frame; for v1 we just snap
            // to ground (drop animation will be hooked up when the kerb GO gets
            // an Update driver). TODO: feed the handle through a per-tick driver
            // service so the visual matches the user spec exactly.
            _ = _drop.PlayDropFromAir(center, 0f, KerbDropHeight);

            Publish(new DynamicKerbSpawnedEvent(id, center, side));
        }

        private void ClearInternal(string reason)
        {
            for (int i = 0; i < _active.Count; i++)
            {
                var k = _active[i];
                _collision.Unregister(k.Id);
                if (k.GameObject != null)
                    _factory.Destroy(k.GameObject);
            }
            _active.Clear();
            Publish(new DynamicKerbsClearedEvent(reason));
        }

        private void EnsureRoot()
        {
            if (_root != null) return;
            _root = _factory.CreateEmpty("[DynamicKerbsRoot]");
        }

        private static Vector2 ToXZ(Vector3 v) => new(v.x, v.z);
    }
}
