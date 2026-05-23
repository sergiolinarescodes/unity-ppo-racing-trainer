using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.AiDriver.Loop;
using UnityPpoRacingTrainer.Core.AiDriver.Physics;
using UnityPpoRacingTrainer.Core.AiDriver.Physics.Draft;
using UnityPpoRacingTrainer.Core.AiDriver.Physics.Fuel;
using UnityPpoRacingTrainer.Core.AiDriver.Physics.Tires;
using UnityPpoRacingTrainer.Core.AiDriver.Policy;
using UnityPpoRacingTrainer.Core.AiDriver.Training;
using Unidad.Core.Registry;
using Unity.MLAgents.Sensors;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.AiDriver.Versions.Manifest
{
    /// <summary>
    /// Plug-in strategy surface for per-version AI driver behavior. Each
    /// interface comes paired with a string-keyed registry; concrete strategies
    /// register themselves at bootstrap, and manifest entries reference them
    /// by id. The single extension recipe for "add a new behavior to one
    /// version without touching the others":
    /// <list type="number">
    /// <item>Write a new class implementing the relevant strategy interface.</item>
    /// <item>Add a small <c>ISystemInstaller</c> that calls
    /// <c>registry.Register(id, instance)</c>.</item>
    /// <item>Reference the id from the manifest of the version(s) that should
    /// receive the behavior. Manifests that don't list the id are unaffected,
    /// preserving frozen-snapshot behavior.</item>
    /// </list>
    /// </summary>

    /// <summary>
    /// Per-car reward stream. Implementations may subscribe to event-bus events,
    /// poll services from the container (via constructor injection from the
    /// installer that registers them), and accumulate either an immediate
    /// per-tick delta (<see cref="AccumulatePerTick"/>) or a pending delta /
    /// terminal-end signal that the composite reward source will drain at the
    /// end of the tick (<see cref="Drain"/>).
    /// </summary>
    public interface IRewardChannel
    {
        /// <summary>Stable id, referenced from manifest <c>rewardChannels[].id</c>.</summary>
        string Id { get; }

        /// <summary>Called when a car's episode begins, before any
        /// <see cref="AccumulatePerTick"/> call for that car.</summary>
        void OnEpisodeBegin(CarId carId);

        /// <summary>Called once per ML decision tick. Returns the per-tick
        /// reward delta and (rarely) a terminal end reason. <paramref name="dt"/>
        /// is wall-clock seconds since the previous tick.</summary>
        StepResult AccumulatePerTick(CarId carId, float dt);

        /// <summary>Called after <see cref="AccumulatePerTick"/> to drain any
        /// reward / end signal accumulated asynchronously via event handlers
        /// (e.g. an overtake event fired between ticks). Implementations that
        /// only use the synchronous per-tick path can return <see cref="StepResult.None"/>.</summary>
        StepResult Drain(CarId carId);

        /// <summary>Called when the agent is being torn down (scene cleanup,
        /// scenario reset). Channels free per-car state here.</summary>
        void OnAgentUnregistered(CarId carId);
    }

    public interface IPhysicsModel
    {
        string Id { get; }
    }

    /// <summary>
    /// Per-version observation writer. Owns the full layout: float count,
    /// per-block sizes, wall-ray angles, lookahead seconds, and the
    /// <c>VectorSensor</c> Write methods called by <c>AiDriverPolicyService</c>.
    /// One implementation per version that needs a different observation shape;
    /// the active writer is resolved by id from the active manifest's
    /// <c>codeModules.observationWriter</c> via <see cref="IObservationWriterRegistry"/>.
    ///
    /// New writer recipe (when changing the observation shape would break a
    /// trained ONNX): implement <see cref="IObservationWriter"/>, register
    /// under a new id (e.g. "RacingV2"), pin <c>LayoutHash</c> to a stable
    /// FNV-1a-style fingerprint of the layout, freeze a new manifest pointing
    /// to the new id + hash. Old manifests keep their pinned id + hash, so
    /// their ONNX continues to load against the original layout.
    /// </summary>
    public interface IObservationWriter
    {
        string Id { get; }

        int FloatsPerFrame { get; }
        int BaseObservationFloats { get; }
        int RaceContextFloats { get; }
        int FrontConeFloats { get; }

        int LookaheadAnchors { get; }
        ReadOnlySpan<float> LookaheadSeconds { get; }
        float LookaheadReferenceSpeed { get; }
        float KappaScale { get; }

        int WallRayCount { get; }
        float WallRayMaxMeters { get; }
        ReadOnlySpan<float> WallRayAnglesRad { get; }

        int OpponentRayCount { get; }
        float ConeHalfAngleRad { get; }

        /// <summary>
        /// Stable, content-derived hash of the layout this writer emits.
        /// Manifests pin this in <c>observation.layoutHash</c>; the snapshot
        /// test loads every manifest and asserts the writer's hash matches.
        /// Drift between writer code and any version's pinned hash fails CI.
        /// </summary>
        string LayoutHash { get; }

        void WriteZeros(VectorSensor sensor);

        int WriteBase(
            Span<float> buf,
            in CarState state,
            in CarParameters p,
            in TrackProjection proj,
            ReadOnlySpan<CenterlineSample> samples,
            float yawRate,
            float prevSteer,
            float prevThrottle,
            ReadOnlySpan<float> wallRayOccupancy,
            float surfaceCode);

        int WriteRaceContext(
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
            DriverPersonality personality);

        int WriteFrontCone(
            Span<float> buf, int offset,
            Vector3 selfPos,
            float selfHeading,
            Vector2 selfVel,
            float maxSpeed,
            ReadOnlySpan<RacingObservationLayout.OtherCar> others,
            ReadOnlySpan<Vector2> othersVel);
    }

    public interface IRewardChannelRegistry : IRegistry<string, IRewardChannel> { }
    public sealed class RewardChannelRegistry : RegistryBase<string, IRewardChannel>, IRewardChannelRegistry { }

    public interface IPhysicsModelRegistry : IRegistry<string, IPhysicsModel> { }
    public sealed class PhysicsModelRegistry : RegistryBase<string, IPhysicsModel>, IPhysicsModelRegistry { }

    public interface IObservationWriterRegistry : IRegistry<string, IObservationWriter> { }
    public sealed class ObservationWriterRegistry : RegistryBase<string, IObservationWriter>, IObservationWriterRegistry { }
}
