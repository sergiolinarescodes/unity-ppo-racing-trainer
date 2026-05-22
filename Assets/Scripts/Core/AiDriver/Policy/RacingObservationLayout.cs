using System;
using UnityPpoRacingTrainer.Core.AiDriver.Loop;
using UnityPpoRacingTrainer.Core.AiDriver.Physics;
using UnityPpoRacingTrainer.Core.AiDriver.Physics.Draft;
using UnityPpoRacingTrainer.Core.AiDriver.Physics.Fuel;
using UnityPpoRacingTrainer.Core.AiDriver.Physics.Tires;
using UnityPpoRacingTrainer.Core.Track;
using UnityPpoRacingTrainer.Core.Track.Loop;
using Unity.MLAgents.Sensors;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.AiDriver.Policy
{
    /// <summary>
    /// Canonical 60-float observation layout. Three semantic blocks:
    /// <see cref="BaseObservationFloats"/> (ego + lookahead + wall feelers +
    /// surface), <see cref="RaceContextFloats"/> (other cars + draft + tires
    /// + fuel + personality), <see cref="FrontConeFloats"/> (front-cone
    /// opponent rays).
    ///
    /// Layout (all values clamped to roughly [-1, 1] unless noted):
    /// <code>
    /// — Base block (25 floats: ego + circuit lookahead + walls + surface) —
    /// [0]  longVel / MaxSpeed
    /// [1]  latVel  / MaxSpeed
    /// [2]  yawRate / π·rad·s⁻¹
    /// [3]  signedLat / halfWidth     (>|1| means off-track)
    /// [4]  headingErr / π
    /// [5]  prevSteer
    /// [6]  prevThrottle
    /// [7..16]  5 anchors × (curvature_norm, halfWidth_norm)
    /// [17..23] 7 wall-distance feeler rays at body-relative angles
    ///          (-90°, -45°, -22.5°, 0°, +22.5°, +45°, +90°)
    /// [24] surface (0 = asphalt, 0.5 = kerb, 1 = off-track)
    /// — Race context block (25 floats: opponents + draft + tires + fuel + driver) —
    /// [25..30] two nearest cars ahead × (rel-dist, rel-bearing, rel-speed)
    /// [31..36] two nearest cars behind × (rel-dist, rel-bearing, rel-speed)
    /// [37]     smoothed draft strength      [0,1]
    /// [38..39] tire wear L, R               [0,1]
    /// [40]     puncture flag (any)          {0,1}
    /// [41]     fuel laps-remaining          [0,1] (5 laps → 1.0)
    /// [42..49] driver personality vector    (6 active + 2 reserved)
    /// — Front cone block (10 floats: forward opponent rays) —
    /// [50..54] 5 front-cone rays: opponent distance occupancy
    ///          (1 = touching, 0 = clear/empty)
    /// [55..59] 5 front-cone rays: closing speed normalized to MaxSpeed
    ///          (signed, +ve = approaching), 0 when ray sees no car
    /// </code>
    /// Stacked 3× under ML-Agents → 180-dim policy input.
    /// </summary>
    public static class RacingObservationLayout
    {
        public const int FloatsPerFrame = 60;
        public const int BaseObservationFloats = 25;
        public const int RaceContextFloats = 25;
        public const int FrontConeFloats = 10;

        public const int LookaheadAnchors = 5;
        public const int WallRayCount = 7;
        public const float WallRayMaxMeters = 16f;

        public static readonly float[] WallRayAnglesRad =
        {
            -Mathf.PI * 0.5f,
            -Mathf.PI * 0.25f,
            -Mathf.PI * 0.125f,
             0f,
             Mathf.PI * 0.125f,
             Mathf.PI * 0.25f,
             Mathf.PI * 0.5f,
        };

        public static readonly float[] LookaheadSeconds = { 0.0f, 0.1f, 0.3f, 0.7f, 1.5f };
        public const float MaxLookaheadSeconds = 1.5f;

        // Reference speed used for lookahead arc-distance AND curvature
        // normalization. Pinned to a historical pre-nerf MaxSpeed (10.5·c)
        // so the policy "sees" the same physical distance and the same
        // curvature magnitude regardless of later physics tuning. Without
        // this pin, dropping MaxSpeed in a future retune would shrink the
        // lookahead arc and weaken the corner-warning signal.
        public const float LookaheadReferenceSpeed = 10.5f * TrackPieceConstants.CarPhysicsCellSize;

        public const int OtherCarsAhead = 2;
        public const int OtherCarsBehind = 2;
        private const float OtherCarMaxMeters = 48f;

        public const int OpponentRayCount = 5;
        public const float ConeHalfAngleRad = 0.7854f; // ~45°
        public const float RayMaxMeters = 32f;
        public const float OpponentHitRadius = 0.85f;

        private const float LateralGAtSaturation = 8.0f;
        private const float ReferenceHalfWidth = 1.5f;

        /// <summary>
        /// Curvature normalisation factor applied before clamping into [-1, 1]
        /// for the lookahead obs channels. Same value <see cref="WriteBase"/>
        /// uses (<c>buf[o] = Clamp(s.Curvature * KappaScale, -1, 1)</c>) —
        /// exposed for diagnostic / overlay code that wants to mirror what
        /// the policy actually sees without re-deriving the magic.
        /// </summary>
        public static readonly float KappaScale =
            (LookaheadReferenceSpeed * LookaheadReferenceSpeed) / LateralGAtSaturation;

        public readonly record struct OtherCar(CarId Id, Vector3 Position, float Heading, float Speed, Vector2 Velocity);

        public static void WriteZeros(VectorSensor sensor)
        {
            for (int i = 0; i < FloatsPerFrame; i++) sensor.AddObservation(0f);
        }

        /// <summary>Body-relative angle of opponent-ray <paramref name="i"/>.</summary>
        public static float OpponentRayAngleRad(int i)
        {
            float t = i / (float)(OpponentRayCount - 1);
            return Mathf.Lerp(-ConeHalfAngleRad, ConeHalfAngleRad, t);
        }

        /// <summary>
        /// Scans <paramref name="others"/> for any car inside the forward cone
        /// of half-angle <paramref name="coneHalfAngleRad"/> within
        /// <paramref name="maxDistMeters"/> whose line-of-sight closing speed
        /// exceeds <paramref name="minClosingMS"/>. Returns the peak closing
        /// speed found and whether any car qualifies. Geometry is shared with
        /// the front-cone observation rays so reward-side and obs-side threat
        /// semantics stay in lockstep — only the range / threshold differ.
        /// </summary>
        public static bool TryFindPeakThreatClosing(
            Vector3 selfPos, float selfHeading, Vector2 selfVel,
            ReadOnlySpan<OtherCar> others,
            float maxDistMeters, float coneHalfAngleRad, float minClosingMS,
            out float peakClosingMS)
        {
            peakClosingMS = 0f;
            float fwdX = Mathf.Sin(selfHeading);
            float fwdZ = Mathf.Cos(selfHeading);
            float coneCos = Mathf.Cos(coneHalfAngleRad);
            float maxDist2 = maxDistMeters * maxDistMeters;
            bool any = false;
            for (int i = 0; i < others.Length; i++)
            {
                var o = others[i];
                if (o.Id.Value == 0) continue;
                float dx = o.Position.x - selfPos.x;
                float dz = o.Position.z - selfPos.z;
                float dist2 = dx * dx + dz * dz;
                if (dist2 > maxDist2 || dist2 < 1e-8f) continue;
                float dist = Mathf.Sqrt(dist2);
                float fwdDot = (dx * fwdX + dz * fwdZ) / dist;
                if (fwdDot < coneCos) continue;
                float losX = dx / dist;
                float losZ = dz / dist;
                float closing = (selfVel.x - o.Velocity.x) * losX
                              + (selfVel.y - o.Velocity.y) * losZ;
                if (closing < minClosingMS) continue;
                any = true;
                if (closing > peakClosingMS) peakClosingMS = closing;
            }
            return any;
        }

        /// <summary>Writes the 25-float base layout. Returns float-count written.</summary>
        public static int WriteBase(
            Span<float> buf,
            in CarState state,
            in CarParameters p,
            in TrackProjection proj,
            ReadOnlySpan<CenterlineSample> samples,
            float yawRate,
            float prevSteer,
            float prevThrottle,
            ReadOnlySpan<float> wallRayOccupancy,
            float surfaceCode)
        {
            if (buf.Length < BaseObservationFloats)
                throw new ArgumentException($"Buffer too small: needs {BaseObservationFloats}, got {buf.Length}.", nameof(buf));

            float maxSpeed = p.MaxSpeed > 0f ? p.MaxSpeed : 1f;

            float fx = Mathf.Sin(state.Heading);
            float fz = Mathf.Cos(state.Heading);
            float longVel = state.VelocityXZ.x * fx + state.VelocityXZ.y * fz;
            float latVel = state.VelocityXZ.x * fz - state.VelocityXZ.y * fx;

            buf[0] = Mathf.Clamp(longVel / maxSpeed, -1.5f, 1.5f);
            buf[1] = Mathf.Clamp(latVel / maxSpeed, -1.5f, 1.5f);
            buf[2] = Mathf.Clamp(yawRate / Mathf.PI, -1.5f, 1.5f);

            float halfWidth = proj.HalfWidth > 0f ? proj.HalfWidth : ReferenceHalfWidth;
            buf[3] = Mathf.Clamp(proj.SignedLateralOffset / halfWidth, -2f, 2f);

            float tangentHeading = Mathf.Atan2(proj.Tangent.x, proj.Tangent.z);
            float headingErr = NormalizeAngle(tangentHeading - state.Heading);
            buf[4] = Mathf.Clamp(headingErr / Mathf.PI, -1f, 1f);

            buf[5] = Mathf.Clamp(prevSteer, -1f, 1f);
            buf[6] = Mathf.Clamp(prevThrottle, -1f, 1f);

            // Curvature normalization pinned to LookaheadReferenceSpeed (the
            // 10.5·c), NOT the current car's MaxSpeed. Keeps the "this corner
            // is sharp" signal magnitude stable across physics retunes.
            // KappaScale is the public mirror of the same value (see field
            // below) so diagnostic overlays use the identical normalisation.
            float kappaScale = KappaScale;
            for (int i = 0; i < LookaheadAnchors; i++)
            {
                int o = 7 + i * 2;
                if (i < samples.Length)
                {
                    var s = samples[i];
                    buf[o + 0] = Mathf.Clamp(s.Curvature * kappaScale, -1f, 1f);
                    buf[o + 1] = Mathf.Clamp(s.HalfWidth / ReferenceHalfWidth, 0f, 2f);
                }
                else
                {
                    buf[o + 0] = 0f;
                    buf[o + 1] = 0f;
                }
            }

            for (int i = 0; i < WallRayCount; i++)
            {
                int dst = 17 + i;
                buf[dst] = (i < wallRayOccupancy.Length)
                    ? Mathf.Clamp01(wallRayOccupancy[i])
                    : 0f;
            }

            buf[24] = Mathf.Clamp(surfaceCode, 0f, 1f);

            return BaseObservationFloats;
        }

        /// <summary>Writes the 25-float race-context block at <paramref name="offset"/>
        /// (other-cars + draft + tires + fuel + driver personality). Returns new write head.</summary>
        public static int WriteRaceContext(
            Span<float> buf, int offset,
            CarId selfId,
            Vector3 selfPos,
            float selfHeading,
            Vector2 selfVel,
            float maxSpeed,
            ReadOnlySpan<OtherCar> others,
            TireState tire,
            FuelState fuel,
            DraftState draft,
            DriverPersonality personality)
        {
            if (buf.Length < offset + RaceContextFloats)
                throw new ArgumentException("buffer too small for race-context block", nameof(buf));

            float safeMaxSpeed = maxSpeed <= 0f ? 1f : maxSpeed;

            Span<OtherCar> ahead = stackalloc OtherCar[OtherCarsAhead];
            Span<OtherCar> behind = stackalloc OtherCar[OtherCarsBehind];
            ClassifyOthers(selfId, selfPos, selfHeading, others, ahead, behind);

            int idx = offset;
            WriteCar(buf, ref idx, ahead[0], selfPos, selfHeading, selfVel, safeMaxSpeed);
            WriteCar(buf, ref idx, ahead[1], selfPos, selfHeading, selfVel, safeMaxSpeed);
            WriteCar(buf, ref idx, behind[0], selfPos, selfHeading, selfVel, safeMaxSpeed);
            WriteCar(buf, ref idx, behind[1], selfPos, selfHeading, selfVel, safeMaxSpeed);

            buf[idx++] = Mathf.Clamp01(draft.Strength);
            buf[idx++] = Mathf.Clamp01(tire.LeftWear);
            buf[idx++] = Mathf.Clamp01(tire.RightWear);
            buf[idx++] = (tire.LeftPunctured || tire.RightPunctured) ? 1f : 0f;
            buf[idx++] = Mathf.Clamp(fuel.RollingLapsRemaining / 5f, 0f, 1f);

            buf[idx++] = personality.TirePreservation;
            buf[idx++] = personality.FuelEconomy;
            buf[idx++] = personality.PassingAggression;
            buf[idx++] = personality.DefendingResolve;
            buf[idx++] = personality.RiskTolerance;
            buf[idx++] = personality.PeakPaceBias;
            buf[idx++] = personality.Reserved0;
            buf[idx++] = personality.Reserved1;

            return idx;
        }

        /// <summary>Writes the 10-float front-cone block at <paramref name="offset"/>
        /// (5 occupancy rays + 5 closing-speed rays). Returns new write head.</summary>
        public static int WriteFrontCone(
            Span<float> buf, int offset,
            Vector3 selfPos,
            float selfHeading,
            Vector2 selfVel,
            float maxSpeed,
            ReadOnlySpan<OtherCar> others,
            ReadOnlySpan<Vector2> othersVel)
        {
            if (buf.Length < offset + FrontConeFloats)
                throw new ArgumentException("buffer too small for front-cone block", nameof(buf));

            float safeMaxSpeed = maxSpeed <= 0f ? 1f : maxSpeed;
            int distHead = offset;
            int closeHead = offset + OpponentRayCount;

            float fwdX = Mathf.Sin(selfHeading);
            float fwdZ = Mathf.Cos(selfHeading);

            for (int r = 0; r < OpponentRayCount; r++)
            {
                float a = OpponentRayAngleRad(r);
                float ca = Mathf.Cos(a);
                float sa = Mathf.Sin(a);
                float dx = fwdX * ca + fwdZ * sa;
                float dz = fwdZ * ca - fwdX * sa;

                float bestT = float.PositiveInfinity;
                int hitIndex = -1;
                for (int i = 0; i < others.Length; i++)
                {
                    var o = others[i];
                    if (o.Id.Value == 0) continue;
                    float ox = o.Position.x - selfPos.x;
                    float oz = o.Position.z - selfPos.z;
                    float t = ox * dx + oz * dz;
                    if (t <= 0f || t > RayMaxMeters) continue;
                    float perp2 = (ox - t * dx) * (ox - t * dx)
                                + (oz - t * dz) * (oz - t * dz);
                    float r2 = OpponentHitRadius * OpponentHitRadius;
                    if (perp2 > r2) continue;

                    float chordHalf = Mathf.Sqrt(Mathf.Max(0f, r2 - perp2));
                    float tEntry = Mathf.Max(0f, t - chordHalf);
                    if (tEntry < bestT)
                    {
                        bestT = tEntry;
                        hitIndex = i;
                    }
                }

                if (hitIndex < 0)
                {
                    buf[distHead + r] = 0f;
                    buf[closeHead + r] = 0f;
                }
                else
                {
                    float distNorm = 1f - Mathf.Clamp01(bestT / RayMaxMeters);
                    buf[distHead + r] = distNorm;

                    Vector2 otherVel = hitIndex < othersVel.Length
                        ? othersVel[hitIndex]
                        : Vector2.zero;
                    float relVx = selfVel.x - otherVel.x;
                    float relVz = selfVel.y - otherVel.y;
                    float closing = relVx * dx + relVz * dz;
                    buf[closeHead + r] = Mathf.Clamp(closing / safeMaxSpeed, -1.5f, 1.5f);
                }
            }

            return offset + FrontConeFloats;
        }

        private static void WriteCar(Span<float> buf, ref int idx, OtherCar car,
            Vector3 selfPos, float selfHeading, Vector2 selfVel, float maxSpeed)
        {
            if (car.Id.Value == 0)
            {
                buf[idx++] = 0f;
                buf[idx++] = 0f;
                buf[idx++] = 0f;
                return;
            }

            Vector2 rel = new(car.Position.x - selfPos.x, car.Position.z - selfPos.z);
            float dist = rel.magnitude;
            buf[idx++] = Mathf.Clamp01(1f - dist / OtherCarMaxMeters);

            Vector2 myFwd = new(Mathf.Sin(selfHeading), Mathf.Cos(selfHeading));
            float bearing = Mathf.Atan2(
                rel.x * myFwd.y - rel.y * myFwd.x,
                rel.x * myFwd.x + rel.y * myFwd.y);
            buf[idx++] = Mathf.Clamp(bearing / Mathf.PI, -1f, 1f);

            // Real closing speed along the line-of-sight to the other car
            // (positive = approaching). Replaces the prior scalar speed
            // difference, which couldn't distinguish "opponent moving away"
            // from "opponent crossing perpendicular at same speed".
            float invDist = dist > 1e-4f ? 1f / dist : 0f;
            float losX = rel.x * invDist;
            float losZ = rel.y * invDist;
            float closing = (selfVel.x - car.Velocity.x) * losX
                          + (selfVel.y - car.Velocity.y) * losZ;
            buf[idx++] = Mathf.Clamp(closing / maxSpeed, -1.5f, 1.5f);
        }

        private static void ClassifyOthers(CarId selfId, Vector3 selfPos, float selfHeading,
            ReadOnlySpan<OtherCar> others, Span<OtherCar> ahead, Span<OtherCar> behind)
        {
            for (int i = 0; i < ahead.Length; i++) ahead[i] = default;
            for (int i = 0; i < behind.Length; i++) behind[i] = default;

            Vector2 myFwd = new(Mathf.Sin(selfHeading), Mathf.Cos(selfHeading));
            for (int i = 0; i < others.Length; i++)
            {
                var o = others[i];
                if (o.Id.Value == selfId.Value) continue;
                Vector2 rel = new(o.Position.x - selfPos.x, o.Position.z - selfPos.z);
                float forward = Vector2.Dot(rel, myFwd);
                float dist = rel.magnitude;
                if (dist > OtherCarMaxMeters) continue;

                if (forward >= 0f) Insert(ahead, o, dist);
                else Insert(behind, o, dist);
            }
        }

        private static void Insert(Span<OtherCar> slots, OtherCar candidate, float candidateDist)
        {
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i].Id.Value == 0) { slots[i] = candidate; return; }
                Vector2 d = new(slots[i].Position.x - candidate.Position.x, slots[i].Position.z - candidate.Position.z);
                float existingDist = d.magnitude;
                if (candidateDist < existingDist)
                {
                    for (int j = slots.Length - 1; j > i; j--) slots[j] = slots[j - 1];
                    slots[i] = candidate;
                    return;
                }
            }
        }

        private static float NormalizeAngle(float a)
        {
            while (a > Mathf.PI) a -= 2f * Mathf.PI;
            while (a < -Mathf.PI) a += 2f * Mathf.PI;
            return a;
        }
    }
}
