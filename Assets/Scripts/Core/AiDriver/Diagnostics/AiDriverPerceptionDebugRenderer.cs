using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.AiDriver.Loop;
using UnityPpoRacingTrainer.Core.AiDriver.Physics;
using UnityPpoRacingTrainer.Core.AiDriver.Policy;
using UnityPpoRacingTrainer.Core.Bootstrap;
using UnityPpoRacingTrainer.Core.Ghost.Simulation;
using UnityPpoRacingTrainer.Core.Track;
using Reflex.Core;
using Unidad.Core.Abstractions;
using Unidad.Core.Bootstrap;
using Unidad.Core.Debugger;
using Unidad.Core.EventBus;
using Unidad.Core.Testing;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.AiDriver.Diagnostics
{
    /// <summary>
    /// Renders the exact perception block the policy reads in
    /// <see cref="AiDriverPolicyService.CollectObservations"/> on top of the
    /// rendered track in the Scene view. Gated by Shift+D
    /// (<see cref="DebugModeService.IsEnabled"/>); silent until toggled.
    /// Uses <see cref="UnityEngine.Debug.DrawLine"/> so no MonoBehaviour or
    /// LineRenderer GameObjects are created — matches the project's
    /// DI-resolved-service rule. Lines persist for one frame, so the overlay
    /// updates live as the ghost drives.
    ///
    /// Every magic number (lookahead times, reference speed, kappa scale,
    /// wall ray angles + range) is pulled from <see cref="RacingObservationLayout"/>
    /// so the overlay never drifts from what the policy actually sees.
    /// </summary>
    internal sealed class AiDriverPerceptionDebugRenderer : ITickable
    {
        private const float CrossHalfSize = 0.4f;
        private const float TangentArrowLen = 1.5f;
        // Above the slab top (≈0.16u) and the half-meter wall height so the
        // overlay floats clearly over the track instead of clipping into it.
        private const float ProjCrossYOffset = 0.6f;
        // Single-frame duration is fine — Tick redraws every frame. depthTest
        // off forces the line to render on top of opaque geometry so the
        // overlay is visible from the iso/top-down game camera without
        // fighting the road mesh.
        private const bool LineDepthTest = false;
        private const float LineDuration = 0f;

        private readonly IGhostDriverService _ghost;
        private readonly ICarSimulationService _carSim;
        private readonly ITrackQueryService _trackQuery;
        private readonly ITrackCollisionService _collision;
        private readonly DebugModeService _debug;

        // Skip-reason tracking so we log each cause exactly once. Without this
        // a missing service would spam the console every frame.
        private bool _loggedConstructed;
        private bool _lastDebugOn;
        private string _lastSkipReason;

        public AiDriverPerceptionDebugRenderer(
            IGhostDriverService ghost,
            ICarSimulationService carSim,
            ITrackQueryService trackQuery,
            DebugModeService debug,
            ITrackCollisionService collision = null)
        {
            _ghost = ghost;
            _carSim = carSim;
            _trackQuery = trackQuery;
            _debug = debug;
            _collision = collision;
        }

        public void Tick(float deltaTime)
        {
            if (!_loggedConstructed)
            {
                _loggedConstructed = true;
                UnityEngine.Debug.Log($"[Perception] renderer alive. debug={_debug != null} ghost={_ghost != null} carSim={_carSim != null} trackQuery={_trackQuery != null} collision={_collision != null}");
            }

            if (_debug == null) { LogSkipOnce("no DebugModeService"); return; }
            if (_debug.IsEnabled != _lastDebugOn)
            {
                _lastDebugOn = _debug.IsEnabled;
                UnityEngine.Debug.Log($"[Perception] debug toggled → {_lastDebugOn}");
            }
            if (!_debug.IsEnabled) return;

            if (_ghost == null || !_ghost.HasSpawned) { LogSkipOnce("ghost not spawned"); return; }
            if (!_carSim.TryGetState(_ghost.GhostId, out var state)) { LogSkipOnce("no car state"); return; }
            if (!_trackQuery.HasPath) { LogSkipOnce("trackQuery.HasPath=false"); return; }
            // Once we get here, clear the skip log so the next real skip prints fresh.
            _lastSkipReason = null;

            var proj = _trackQuery.Project(state.Position, state.LastAnchorIndex);
            DrawProjection(proj, state.Position);
            DrawLookahead(proj);
            DrawWallRays(state);
        }

        private void LogSkipOnce(string reason)
        {
            if (_lastSkipReason == reason) return;
            _lastSkipReason = reason;
            UnityEngine.Debug.Log($"[Perception] skipping draw: {reason}");
        }

        private void DrawProjection(in TrackProjection proj, Vector3 carPos)
        {
            // Projection point — colour by surface; off-track always wins.
            Color projColour = proj.IsOffTrack
                ? Color.red
                : proj.Surface == SurfaceKind.Kerb ? Color.yellow : Color.white;
            Vector3 p = proj.ProjectedPoint + Vector3.up * ProjCrossYOffset;
            DrawCross(p, projColour, CrossHalfSize);

            // Tangent — short forward arrow showing the policy's "this is
            // forward along the road" reference.
            Vector3 t = new(proj.Tangent.x, 0f, proj.Tangent.z);
            if (t.sqrMagnitude > 1e-6f) t.Normalize();
            DrawLine(p, p + t * TangentArrowLen, projColour);

            // Lateral offset — line from the centreline projection to the car.
            // Length = the signed-lateral channel the model sees as buf[3].
            Vector3 carP = carPos + Vector3.up * ProjCrossYOffset;
            DrawLine(p, carP, projColour);
        }

        private void DrawLookahead(in TrackProjection proj)
        {
            int n = RacingObservationLayout.LookaheadAnchors;
            Span<float> offsets = stackalloc float[n];
            Span<CenterlineSample> samples = stackalloc CenterlineSample[n];

            // Mirror AiDriverPolicyService.CollectObservations:333–341 exactly
            // so the overlay shows the same arc-distance the policy queries.
            float pathLen = _trackQuery.TotalPathLength;
            float capMeters = pathLen > 0f
                ? (_trackQuery.HasLoop ? pathLen * 0.5f : pathLen)
                : float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                float raw = RacingObservationLayout.LookaheadSeconds[i]
                          * RacingObservationLayout.LookaheadReferenceSpeed;
                offsets[i] = Mathf.Min(raw, capMeters);
            }
            _trackQuery.SampleLookaheadAt(proj.NearestAnchorIndex, offsets, samples);

            Vector3? prev = null;
            for (int i = 0; i < n; i++)
            {
                var s = samples[i];
                Vector3 c = s.Position + Vector3.up * ProjCrossYOffset;

                // Colour ramps green (straight) → red (saturated curvature),
                // using the same KappaScale the obs writer applies. >|1.0|
                // after normalisation = the curve maxed out the model's
                // curvature channel.
                float kappa = Mathf.Abs(s.Curvature) * RacingObservationLayout.KappaScale;
                float t01 = Mathf.Clamp01(kappa);
                Color sampleColour = Color.Lerp(Color.green, Color.red, t01);

                DrawCross(c, sampleColour, CrossHalfSize * 0.8f);

                // Half-width bar through the sample, perpendicular to its
                // tangent. Shows the road width the model sees at this
                // lookahead distance.
                Vector3 tan = new(s.Tangent.x, 0f, s.Tangent.z);
                if (tan.sqrMagnitude > 1e-6f) tan.Normalize();
                Vector3 right = new(tan.z, 0f, -tan.x);
                Vector3 a = c + right * s.HalfWidth;
                Vector3 b = c - right * s.HalfWidth;
                DrawLine(a, b, sampleColour);

                // Polyline through the samples — reveals the chain
                // extrapolation past the open-chain tail (linear ray) vs
                // the curved interior.
                if (prev.HasValue)
                    DrawLine(prev.Value, c, Color.white);
                prev = c;
            }
        }

        private void DrawWallRays(in CarState state)
        {
            if (_collision == null) return;
            int n = RacingObservationLayout.WallRayCount;
            float maxR = RacingObservationLayout.WallRayMaxMeters;
            Vector2 originXZ = new(state.Position.x, state.Position.z);
            Vector3 originW = new(state.Position.x, state.Position.y + ProjCrossYOffset, state.Position.z);
            float h = state.Heading;
            float ch = Mathf.Cos(h);
            float sh = Mathf.Sin(h);
            for (int i = 0; i < n; i++)
            {
                float a = RacingObservationLayout.WallRayAnglesRad[i];
                float ca = Mathf.Cos(a);
                float sa = Mathf.Sin(a);
                float dx = sh * ca + ch * sa;
                float dz = ch * ca - sh * sa;
                float d = _collision.RaycastWall(originXZ, new Vector2(dx, dz), maxR);
                float occ = 1f - Mathf.Clamp01(d / maxR);
                Color rayColour = Color.Lerp(Color.white, Color.red, occ);
                Vector3 endW = originW + new Vector3(dx * d, 0f, dz * d);
                DrawLine(originW, endW, rayColour);
            }
        }

        private static void DrawCross(Vector3 c, Color colour, float halfSize)
        {
            DrawLine(c + new Vector3(-halfSize, 0f, 0f), c + new Vector3(halfSize, 0f, 0f), colour);
            DrawLine(c + new Vector3(0f, 0f, -halfSize), c + new Vector3(0f, 0f, halfSize), colour);
            DrawLine(c + new Vector3(0f, -halfSize * 0.5f, 0f), c + new Vector3(0f, halfSize * 0.5f, 0f), colour);
        }

        // Single point where the depth-test-off + zero-duration choice lives.
        // Diagnostic overlays should always be visible regardless of which
        // opaque geometry sits between them and the camera.
        private static void DrawLine(Vector3 a, Vector3 b, Color colour)
            => UnityEngine.Debug.DrawLine(a, b, colour, LineDuration, LineDepthTest);
    }

    public sealed class AiDriverPerceptionDebugSystemInstaller : ISystemInstaller
    {
        public void Install(ContainerBuilder builder)
        {
            builder.AddSingleton(c => new AiDriverPerceptionDebugRenderer(
                    c.Resolve<IGhostDriverService>(),
                    c.Resolve<ICarSimulationService>(),
                    c.Resolve<ITrackQueryService>(),
                    c.Resolve<DebugModeService>(),
                    c.TryResolveOptional<ITrackCollisionService>()),
                typeof(AiDriverPerceptionDebugRenderer),
                typeof(ITickable));
        }

        public ISystemTestFactory CreateTestFactory() => new AiDriverPerceptionDebugTestFactory();
    }

    internal sealed class AiDriverPerceptionDebugTestFactory : ISystemTestFactory
    {
        public Type[] TestedServices => new[] { typeof(AiDriverPerceptionDebugRenderer) };
        public object CreateForTesting(TestDependencies deps) => null;
        public IEnumerable<ITestScenario> GetScenarios() { yield break; }
    }
}
