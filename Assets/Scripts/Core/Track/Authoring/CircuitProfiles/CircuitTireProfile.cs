using System;
using UnityPpoRacingTrainer.Core.AiDriver.Loop;
using UnityPpoRacingTrainer.Core.AiDriver.Physics;
using UnityPpoRacingTrainer.Core.AiDriver.Physics.Tires;
using Unidad.Core.EventBus;
using Unidad.Core.Systems;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Track.Authoring.CircuitProfiles
{
    public interface ICircuitTireProfileService
    {
        /// <summary>Stress coefficient for a given arc-length bucket on the current loop. Returns 1 if unknown.</summary>
        float GetStressCoefficient(float arcLengthAlong);

        /// <summary>(Re)build the per-arc stress buckets from the active loop's centerline.</summary>
        void Rebuild();
    }

    /// <summary>
    /// Per-circuit tire-stress provider. Wires into <see cref="ITirePhysicsService"/>
    /// so wear formulas can scale by location-on-track. v1 ships flat (coefficient
    /// 1.0 everywhere) — the algorithm needs centerline samples that the current
    /// <see cref="ITrackQueryService"/> doesn't expose. Designed so a follow-up
    /// patch that adds <c>CenterlineSamples</c> to the query API only needs to
    /// fill in <see cref="Rebuild"/>; everything downstream (tire service hook,
    /// installer, modifier composition) already calls through this contract.
    /// </summary>
    internal sealed class CircuitTireProfileService : SystemServiceBase, ICircuitTireProfileService
    {
        private const float BucketLength = 4f;
        private const float StraightWeight = 0.6f;
        private const float CurvatureWeight = 0.8f;

        private readonly ITrackQueryService _trackQuery;
        private float[] _buckets;
        private float _totalLength;

        public CircuitTireProfileService(IEventBus eventBus, ITrackQueryService trackQuery,
            ITirePhysicsService tireService)
            : base(eventBus)
        {
            _trackQuery = trackQuery;
            tireService?.SetCircuitStressProvider((_, arc) => GetStressCoefficient(arc));
            Rebuild();
        }

        public float GetStressCoefficient(float arcLengthAlong)
        {
            if (_buckets == null || _buckets.Length == 0 || _totalLength <= 0f) return 1f;
            float a = ((arcLengthAlong % _totalLength) + _totalLength) % _totalLength;
            int idx = Mathf.Clamp((int)(a / BucketLength), 0, _buckets.Length - 1);
            return _buckets[idx];
        }

        public void Rebuild()
        {
            if (_trackQuery == null || !_trackQuery.HasLoop) { _buckets = null; _totalLength = 0f; return; }

            // Sample-via-API: walk a large lookahead at 0.5m spacing and detect
            // wrap by watching arc-length reset on Project(). One-shot cost at
            // loop close; safe to redo on lap-line if loop edits land later.
            const float spacing = 0.5f;
            const int maxSamples = 4096;
            Span<CenterlineSample> window = stackalloc CenterlineSample[maxSamples];
            _trackQuery.SampleLookahead(0, spacing * maxSamples, maxSamples, window);

            // Detect loop length by finding the first index where the position
            // returns near sample[0] after at least 1/3 of the sweep.
            int closeIdx = -1;
            Vector3 p0 = window[0].Position;
            for (int i = maxSamples / 3; i < maxSamples; i++)
            {
                Vector3 d = window[i].Position - p0;
                if (new Vector2(d.x, d.z).sqrMagnitude < 0.25f) { closeIdx = i; break; }
            }
            int n = closeIdx > 0 ? closeIdx : maxSamples;

            var arc = new float[n];
            var yaw = new float[n];
            for (int i = 1; i < n; i++)
            {
                Vector3 d = window[i].Position - window[i - 1].Position;
                arc[i] = arc[i - 1] + new Vector2(d.x, d.z).magnitude;
                if (i >= 2)
                {
                    Vector2 a0 = new(window[i - 1].Position.x - window[i - 2].Position.x,
                                     window[i - 1].Position.z - window[i - 2].Position.z);
                    Vector2 a1 = new(window[i].Position.x - window[i - 1].Position.x,
                                     window[i].Position.z - window[i - 1].Position.z);
                    if (a0.sqrMagnitude > 1e-6f && a1.sqrMagnitude > 1e-6f)
                    {
                        float th0 = Mathf.Atan2(a0.x, a0.y);
                        float th1 = Mathf.Atan2(a1.x, a1.y);
                        yaw[i] = Mathf.Abs(Mathf.DeltaAngle(th0 * Mathf.Rad2Deg, th1 * Mathf.Rad2Deg) * Mathf.Deg2Rad);
                    }
                }
            }
            _totalLength = arc[n - 1];

            float runUp = 0f;
            var straightBefore = new float[n];
            for (int i = 0; i < n; i++)
            {
                if (yaw[i] < 0.05f) runUp += i > 0 ? (arc[i] - arc[i - 1]) : 0f;
                else runUp = 0f;
                straightBefore[i] = runUp;
            }

            int bucketCount = Mathf.Max(1, Mathf.CeilToInt(_totalLength / BucketLength));
            _buckets = new float[bucketCount];
            for (int i = 0; i < bucketCount; i++) _buckets[i] = 1f;

            for (int i = 0; i < n; i++)
            {
                int idx = Mathf.Clamp((int)(arc[i] / BucketLength), 0, bucketCount - 1);
                float curvatureTerm = Mathf.Min(1f, yaw[i] / 0.4f);
                float straightTerm = Mathf.Clamp01(straightBefore[i] / 30f);
                float stress = 1f + CurvatureWeight * curvatureTerm + StraightWeight * straightTerm;
                if (stress > _buckets[idx]) _buckets[idx] = stress;
            }
        }
    }
}
