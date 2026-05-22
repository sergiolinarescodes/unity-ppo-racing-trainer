using System.Collections.Generic;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Track.Ribbon
{
    /// <summary>
    /// Resamples a sequence of <see cref="TrackChainAnchor"/>s into a smooth curve
    /// using cubic Hermite interpolation. Per-segment endpoint tangents come from
    /// each anchor's own <c>Tangent</c> field (so the curve is C1-continuous at port
    /// joins) scaled by the segment chord length — equivalent to centripetal Catmull-Rom
    /// when chord lengths are uniform, but better behaved when the chain mixes long
    /// straights with short curve segments.
    /// </summary>
    public static class CatmullRomSpline
    {
        public readonly struct Sample
        {
            public readonly Vector3 Position;
            public readonly Vector3 Tangent;
            public readonly float HalfWidth;
            public Sample(Vector3 p, Vector3 t, float hw)
            {
                Position = p; Tangent = t; HalfWidth = hw;
            }
        }

        /// <summary>
        /// Walks the chain and emits samples spaced approximately <paramref name="arcStep"/>
        /// world units apart. Returns at least the two end anchors when the chain is
        /// shorter than one step. The caller is responsible for ensuring anchors are
        /// in chain order (start → end) and that tangents face along the chain.
        /// </summary>
        public static List<Sample> Resample(IReadOnlyList<TrackChainAnchor> anchors, float arcStep)
        {
            var output = new List<Sample>();
            if (anchors == null || anchors.Count == 0) return output;
            if (anchors.Count == 1)
            {
                output.Add(new Sample(anchors[0].WorldPos, anchors[0].Tangent, anchors[0].HalfWidth));
                return output;
            }
            if (arcStep <= 1e-4f) arcStep = 0.1f;

            for (int i = 0; i < anchors.Count - 1; i++)
            {
                var a = anchors[i];
                var b = anchors[i + 1];
                float chord = Vector3.Distance(a.WorldPos, b.WorldPos);
                if (chord < 1e-4f) continue;

                // Hermite tangents are scaled by chord length so the curve's velocity
                // matches the segment scale (a long straight gets a long m, a short
                // arc segment a short one) — keeps the curve well-conditioned.
                Vector3 m0 = (a.Tangent.sqrMagnitude > 1e-6f ? a.Tangent.normalized : (b.WorldPos - a.WorldPos).normalized) * chord;
                Vector3 m1 = (b.Tangent.sqrMagnitude > 1e-6f ? b.Tangent.normalized : (b.WorldPos - a.WorldPos).normalized) * chord;

                int segSteps = Mathf.Max(1, Mathf.CeilToInt(chord / arcStep));
                int firstStep = (i == 0) ? 0 : 1; // skip duplicate at segment seam
                for (int s = firstStep; s <= segSteps; s++)
                {
                    float t = (float)s / segSteps;
                    Vector3 p = Hermite(a.WorldPos, m0, b.WorldPos, m1, t);
                    Vector3 d = HermiteDerivative(a.WorldPos, m0, b.WorldPos, m1, t).normalized;
                    if (d.sqrMagnitude < 1e-6f) d = (b.WorldPos - a.WorldPos).normalized;
                    float hw = Mathf.Lerp(a.HalfWidth, b.HalfWidth, t);
                    output.Add(new Sample(p, d, hw));
                }
            }
            return output;
        }

        private static Vector3 Hermite(Vector3 p0, Vector3 m0, Vector3 p1, Vector3 m1, float t)
        {
            float t2 = t * t;
            float t3 = t2 * t;
            float h00 = 2f * t3 - 3f * t2 + 1f;
            float h10 = t3 - 2f * t2 + t;
            float h01 = -2f * t3 + 3f * t2;
            float h11 = t3 - t2;
            return h00 * p0 + h10 * m0 + h01 * p1 + h11 * m1;
        }

        private static Vector3 HermiteDerivative(Vector3 p0, Vector3 m0, Vector3 p1, Vector3 m1, float t)
        {
            float t2 = t * t;
            float dh00 = 6f * t2 - 6f * t;
            float dh10 = 3f * t2 - 4f * t + 1f;
            float dh01 = -6f * t2 + 6f * t;
            float dh11 = 3f * t2 - 2f * t;
            return dh00 * p0 + dh10 * m0 + dh01 * p1 + dh11 * m1;
        }
    }
}
