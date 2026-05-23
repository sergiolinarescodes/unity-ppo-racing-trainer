using System;
using UnityPpoRacingTrainer.Core.AiDriver.Loop;
using UnityPpoRacingTrainer.Core.AiDriver.Physics;
using UnityPpoRacingTrainer.Core.AiDriver.Physics.Draft;
using UnityPpoRacingTrainer.Core.AiDriver.Physics.Fuel;
using UnityPpoRacingTrainer.Core.AiDriver.Physics.Tires;
using UnityPpoRacingTrainer.Core.AiDriver.Versions.Manifest;
using Unity.MLAgents.Sensors;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.AiDriver.Policy.Observation
{
    /// <summary>
    /// Canonical 60-float observation writer (V1 layout). Implements
    /// <see cref="IObservationWriter"/> by forwarding every call to the
    /// existing <see cref="RacingObservationLayout"/> static methods, which
    /// remain the authoritative implementation of the V1 layout. Diagnostic /
    /// overlay / visualizer code still consumes the static class directly;
    /// <c>AiDriverPolicyService</c> (the hot observation path) consumes this
    /// writer through DI so a future <c>RacingV2ObservationWriter</c> can be
    /// dropped in by registering under a new id without touching the policy
    /// service.
    /// </summary>
    public sealed class RacingV1ObservationWriter : IObservationWriter
    {
        public static readonly RacingV1ObservationWriter Instance = new RacingV1ObservationWriter();

        public string Id => "RacingV1";

        public int FloatsPerFrame => RacingObservationLayout.FloatsPerFrame;
        public int BaseObservationFloats => RacingObservationLayout.BaseObservationFloats;
        public int RaceContextFloats => RacingObservationLayout.RaceContextFloats;
        public int FrontConeFloats => RacingObservationLayout.FrontConeFloats;

        public int LookaheadAnchors => RacingObservationLayout.LookaheadAnchors;
        public ReadOnlySpan<float> LookaheadSeconds => RacingObservationLayout.LookaheadSeconds;
        public float LookaheadReferenceSpeed => RacingObservationLayout.LookaheadReferenceSpeed;
        public float KappaScale => RacingObservationLayout.KappaScale;

        public int WallRayCount => RacingObservationLayout.WallRayCount;
        public float WallRayMaxMeters => RacingObservationLayout.WallRayMaxMeters;
        public ReadOnlySpan<float> WallRayAnglesRad => RacingObservationLayout.WallRayAnglesRad;

        public int OpponentRayCount => RacingObservationLayout.OpponentRayCount;
        public float ConeHalfAngleRad => RacingObservationLayout.ConeHalfAngleRad;

        public string LayoutHash => RacingObservationLayout.ComputeLayoutHash();

        public void WriteZeros(VectorSensor sensor) => RacingObservationLayout.WriteZeros(sensor);

        public int WriteBase(
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
            => RacingObservationLayout.WriteBase(buf, state, p, proj, samples,
                yawRate, prevSteer, prevThrottle, wallRayOccupancy, surfaceCode);

        public int WriteRaceContext(
            Span<float> buf, int offset,
            CarId selfId,
            Vector3 selfPos,
            float selfHeading,
            Vector2 selfVel,
            float maxSpeed,
            ReadOnlySpan<RacingObservationLayout.OtherCar> others,
            TireState tire,
            FuelState fuel,
            DraftState draft,
            DriverPersonality personality)
            => RacingObservationLayout.WriteRaceContext(buf, offset, selfId, selfPos,
                selfHeading, selfVel, maxSpeed, others, tire, fuel, draft, personality);

        public int WriteFrontCone(
            Span<float> buf, int offset,
            Vector3 selfPos,
            float selfHeading,
            Vector2 selfVel,
            float maxSpeed,
            ReadOnlySpan<RacingObservationLayout.OtherCar> others,
            ReadOnlySpan<Vector2> othersVel)
            => RacingObservationLayout.WriteFrontCone(buf, offset, selfPos, selfHeading,
                selfVel, maxSpeed, others, othersVel);
    }
}
