using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.AiDriver.Loop;
using UnityPpoRacingTrainer.Core.AiDriver.Physics;
using UnityPpoRacingTrainer.Core.AiDriver.Physics.Draft;
using UnityPpoRacingTrainer.Core.AiDriver.Physics.Fuel;
using UnityPpoRacingTrainer.Core.AiDriver.Physics.Tires;
using UnityPpoRacingTrainer.Core.AiDriver.Race;
using UnityPpoRacingTrainer.Core.AiDriver.Training;
using UnityPpoRacingTrainer.Core.AiDriver.Training.Stages;
using UnityPpoRacingTrainer.Core.AiDriver.Versions;
using UnityPpoRacingTrainer.Core.Track.Loop;
using Unidad.Core.EventBus;
using Unidad.Core.Systems;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.AiDriver.Policy
{
    public enum SpawnStrategy
    {
        Random,
        LapStartAnchor,
        LongestStraightMidpoint,
    }

    /// <summary>
    /// Bridges car simulation + track query to the ML-Agents action/observation
    /// surface. Reward + episode-end logic delegates to <see cref="IEpisodeRewardSource"/>;
    /// version-specific observation shape, physics defaults, and reward shaping
    /// are resolved through <see cref="IAiDriverVersionProfile"/> — there are
    /// no version flags on this service.
    /// </summary>
    internal sealed class AiDriverPolicyService : SystemServiceBase, IAiDriverPolicyService
    {
        private const float DecisionDtSeconds = 0.10f;
        private const float HeuristicLookaheadCells = 2.0f;
        private const float HeuristicSteerGain = 2.0f;
        private const float HeuristicMaxThrottle = 1.0f;

        public float HeadingJitterRad { get; set; } = 0.02f;
        public SpawnStrategy Spawn { get; set; } = SpawnStrategy.LongestStraightMidpoint;

        private readonly ICarSimulationService _carSim;
        private readonly ITrackQueryService _trackQuery;
        private readonly IClosedLoopService _loop;
        private readonly DriverProfileRegistry _profiles;
        private readonly Track.ITrackCollisionService _collision;
        private readonly ITirePhysicsService _tires;
        private readonly IFuelService _fuel;
        private readonly IDraftService _draft;
        private readonly IAiDriverVersionProfile _versionProfile;
        private readonly Training.IStageIdProvider _stage;
        private readonly IActiveStageProfile _active;
        // Optional. When non-null + IsRaceScoped, ApplyActions overrides the
        // policy's steer/throttle to zero for drivers in Finished/Eliminated
        // state so a resolved car coasts to a stop instead of continuing to
        // race. The agent's PPO episode stays open (no EndEpisode); the
        // resolved tail is purely a presentation + reward-zeroing concern.
        private readonly IRaceCoordinator _coord;

        private IEpisodeRewardSource _rewards = NullEpisodeRewardSource.Instance;
        private readonly Dictionary<CarId, AgentRecord> _agents = new();
        private readonly Dictionary<CarId, float[]> _lastObservations = new();

        public AiDriverPolicyService(
            IEventBus eventBus,
            ICarSimulationService carSim,
            ITrackQueryService trackQuery,
            IClosedLoopService loop,
            DriverProfileRegistry profiles,
            IAiDriverVersionProfile versionProfile,
            Track.ITrackCollisionService collision = null,
            ITirePhysicsService tires = null,
            IFuelService fuel = null,
            IDraftService draft = null,
            Training.IStageIdProvider stage = null,
            IActiveStageProfile active = null,
            IRaceCoordinator coord = null) : base(eventBus)
        {
            _carSim = carSim;
            _trackQuery = trackQuery;
            _loop = loop;
            _profiles = profiles;
            _versionProfile = versionProfile ?? throw new ArgumentNullException(nameof(versionProfile));
            _collision = collision;
            _tires = tires;
            _fuel = fuel;
            _draft = draft;
            _stage = stage;
            _active = active;
            _coord = coord;

            Subscribe<DriverPersonalityChangedEvent>(OnPersonalityChanged);
        }

        // True iff this service is running inside a ML-Agents training session
        // (an EnvironmentParameters resolver is attached). Inference scenes
        // and Scenario Browser scenes leave Resolver null and therefore see
        // the full grid + opponent observations regardless of stage profile.
        private bool IsInTraining => _stage != null && _stage.Resolver != null;

        // Warmup mode is training stage 0 — declared by the active stage
        // profile via ExpectedOpponentCount == 0. Inference scenes (no
        // resolver) always return false so the full grid + collisions show.
        private bool IsWarmup
        {
            get
            {
                if (!IsInTraining) return false;
                if (_active != null) return _active.Current.ExpectedOpponentCount == 0;
                return _stage.Resolve() == 0;
            }
        }

        private void OnPersonalityChanged(DriverPersonalityChangedEvent e)
        {
            if (!_agents.TryGetValue(e.Id, out var rec)) return;
            _agents[e.Id] = rec with
            {
                Profile = rec.Profile with { Personality = e.Personality }
            };
        }

        public CarId RegisterAgent(string profileId, Vector3 spawnPosition, float heading)
        {
            var snapshot = _profiles.Get(profileId);
            var carId = _carSim.Spawn(spawnPosition, heading, snapshot.Car);
            var rng = new System.Random(carId.Value.GetHashCode() ^ 0x5BD1E995);
            _agents[carId] = new AgentRecord(snapshot, spawnPosition, heading, Rng: rng);
            return carId;
        }

        public void BeginEpisode(CarId carId)
        {
            if (!_agents.TryGetValue(carId, out var rec)) return;

            Vector3 spawnPos = rec.SpawnPosition;
            float spawnHeading = rec.SpawnHeading;
            int baseAnchorIdx = -1;
            ClosedLoop? loopRef = null;
            if (_loop.TryGetCurrentLoop(out var loop) && loop.Anchors != null && loop.Anchors.Count > 0)
            {
                loopRef = loop;
                switch (Spawn)
                {
                    case SpawnStrategy.Random:
                    {
                        int idx = rec.Rng.Next(loop.Anchors.Count);
                        var anchor = loop.Anchors[idx];
                        Vector3 t = anchor.Tangent.sqrMagnitude > 1e-6f ? anchor.Tangent.normalized : Vector3.forward;
                        spawnPos = anchor.WorldPos - ClosedLoop.DefaultSpawnBackOffset * t;
                        float baseHeading = Mathf.Atan2(t.x, t.z);
                        float jitter = (float)((rec.Rng.NextDouble() * 2.0 - 1.0) * HeadingJitterRad);
                        spawnHeading = baseHeading + jitter;
                        baseAnchorIdx = idx;
                        break;
                    }
                    case SpawnStrategy.LapStartAnchor:
                    case SpawnStrategy.LongestStraightMidpoint:
                    default:
                    {
                        var pose = loop.GetCanonicalStartPose();
                        spawnPos = pose.position;
                        float baseHeading = loop.GetCanonicalStartHeading();
                        float jitter = (float)((rec.Rng.NextDouble() * 2.0 - 1.0) * HeadingJitterRad);
                        spawnHeading = baseHeading + jitter;
                        baseAnchorIdx = loop.LapStartAnchorIndex;
                        break;
                    }
                }
            }

            (float lateral, float back) = ComputeSpawnOffset(rec.GridSlot, rec.Rng);
            ApplyGridOffset(lateral, back, loopRef, baseAnchorIdx, ref spawnPos, ref spawnHeading);

            _carSim.TeleportTo(carId, spawnPos, spawnHeading);
            _agents[carId] = rec with
            {
                PrevHeading = spawnHeading,
                HasPrevHeading = false,
                Smoother = new ActionSmoother(),
            };
            _rewards.OnEpisodeBegin(carId);
        }

        public void SetSpawnPose(CarId carId, Vector3 spawnPosition, float heading)
        {
            if (!_agents.TryGetValue(carId, out var rec)) return;
            _agents[carId] = rec with { SpawnPosition = spawnPosition, SpawnHeading = heading };
        }

        public void RespawnAllAtStartLine(Vector3 spawnPosition, float heading)
        {
            var keys = new CarId[_agents.Count];
            int i = 0;
            foreach (var k in _agents.Keys) keys[i++] = k;

            ClosedLoop? loopRef = _loop.TryGetCurrentLoop(out var loop)
                                  && loop.Anchors != null && loop.Anchors.Count > 0
                ? loop
                : (ClosedLoop?)null;
            int baseIdx = loopRef.HasValue ? loopRef.Value.LapStartAnchorIndex : -1;

            foreach (var k in keys)
            {
                if (!_agents.TryGetValue(k, out var rec)) continue;
                _agents[k] = rec with
                {
                    SpawnPosition = spawnPosition,
                    SpawnHeading = heading,
                };
                Vector3 perCarPos = spawnPosition;
                float perCarHeading = heading;
                (float lateral, float back) = ComputeSpawnOffset(rec.GridSlot, rec.Rng);
                ApplyGridOffset(lateral, back, loopRef, baseIdx, ref perCarPos, ref perCarHeading);
                _carSim.TeleportTo(k, perCarPos, perCarHeading);
            }
        }

        public DriverProfileSnapshot GetProfile(CarId carId)
            => _agents.TryGetValue(carId, out var rec) ? rec.Profile : DriverProfileSnapshot.Default;

        // 2 cars per row (pairs per column), so 12 cars = 6 rows on the grid.
        private const int GridColumns = 2;
        // Tightened from 0.7 → 0.5 so outer columns stay clear of typical
        // track walls on narrow procedural loops; then ×1.2 (→ 0.6) to give
        // side-by-side row partners more breathing room at the start.
        private const float GridLateralStep = 0.6f;
        // Tightened from 2.5 → 1.2 so back rows of large packs (≥ 24 cars)
        // still spawn near the start arc instead of wrapping around the loop.
        // Then ×1.5 (→ 1.8) so row N is meaningfully ahead of row N+1 —
        // gives lap-1 traffic more room before the field tangles.
        private const float GridRowSpacing = 1.8f;
        // Per-car ±lateral jitter applied during stage-0 warmup so all-same-arc
        // spawns diverge instantly without waiting for policy action noise.
        private const float WarmupLateralJitter = 0.05f;

        public void SetGridSlot(CarId carId, int slot)
        {
            if (!_agents.TryGetValue(carId, out var rec)) return;
            _agents[carId] = rec with { GridSlot = Mathf.Max(0, slot) };
        }

        // Translates a grid slot (or warmup jitter) into (lateral, back)
        // offsets in cell-units relative to the lap-start arc. Stage-0 warmup
        // collapses the whole grid onto baseArc with a tiny lateral jitter so
        // every agent starts on the same line but diverges immediately.
        private (float lateral, float back) ComputeSpawnOffset(int slot, System.Random rng)
        {
            if (IsWarmup)
            {
                float j = rng != null
                    ? (float)((rng.NextDouble() * 2.0 - 1.0) * WarmupLateralJitter)
                    : 0f;
                return (j, 0f);
            }
            if (slot <= 0) return (0f, 0f);
            int col = slot % GridColumns;
            int row = slot / GridColumns;
            float lateral = (col - (GridColumns - 1) * 0.5f) * GridLateralStep;
            float back = row * GridRowSpacing;
            return (lateral, back);
        }

        private static void ApplyGridOffset(float lateral, float back, ClosedLoop? loop, int baseAnchorIdx, ref Vector3 pos, ref float heading)
        {
            if (lateral == 0f && back == 0f) return;

            if (loop.HasValue && baseAnchorIdx >= 0
                && loop.Value.CumulativeArcLength != null
                && loop.Value.CumulativeArcLength.Count > baseAnchorIdx
                && loop.Value.TotalLength > 0f)
            {
                var L = loop.Value;
                float baseArc = L.CumulativeArcLength[baseAnchorIdx] - ClosedLoop.DefaultSpawnBackOffset;
                var pose = L.SamplePoseAtArc(baseArc - back);
                Vector3 fwd = pose.rotation * Vector3.forward;
                Vector3 right = new Vector3(fwd.z, 0f, -fwd.x);
                if (right.sqrMagnitude > 1e-6f) right.Normalize();
                pos = pose.position + right * lateral;
                heading = Mathf.Atan2(fwd.x, fwd.z);
                return;
            }

            float sh = Mathf.Sin(heading);
            float ch = Mathf.Cos(heading);
            pos += new Vector3(-sh * back + ch * lateral, 0f, -ch * back - sh * lateral);
        }

        public void CollectObservations(CarId carId, VectorSensor sensor)
        {
            if (!_agents.TryGetValue(carId, out var rec)
                || !_carSim.TryGetState(carId, out var state))
            {
                RacingObservationLayout.WriteZeros(sensor);
                return;
            }

            TrackProjection proj = _trackQuery.HasPath
                ? _trackQuery.Project(state.Position, state.LastAnchorIndex)
                : default;

            int lookaheadCount = RacingObservationLayout.LookaheadAnchors;
            float[] lookaheadSecs = RacingObservationLayout.LookaheadSeconds;
            Span<CenterlineSample> samples = stackalloc CenterlineSample[lookaheadCount];
            if (_trackQuery.HasPath)
            {
                // Lookahead distance uses LookaheadReferenceSpeed so the agent's
                // visible horizon is invariant to physics MaxSpeed tuning.
                float lookaheadRefSpeed = RacingObservationLayout.LookaheadReferenceSpeed;
                Span<float> offsets = stackalloc float[lookaheadCount];
                float pathLen = _trackQuery.TotalPathLength;
                // Closed loop: half-total caps wrap-around overlap (longest
                // visible horizon = half a lap, the rest is behind you).
                // Open ribbon: full length is the cap; the lookahead samples
                // clamp at the chain's tail so the model sees a flat plateau,
                // not zero-padding (which collapses throttle to brake).
                float capMeters = pathLen > 0f
                    ? (_trackQuery.HasLoop ? pathLen * 0.5f : pathLen)
                    : float.MaxValue;
                for (int i = 0; i < lookaheadCount; i++)
                {
                    float raw = lookaheadSecs[i] * lookaheadRefSpeed;
                    offsets[i] = Mathf.Min(raw, capMeters);
                }
                _trackQuery.SampleLookaheadAt(proj.NearestAnchorIndex, offsets, samples);
            }
            else
            {
                samples.Clear();
            }

            float yawRate = 0f;
            if (rec.HasPrevHeading)
            {
                float dHead = NormalizeAngle(state.Heading - rec.PrevHeading);
                yawRate = dHead / DecisionDtSeconds;
            }

            int rayCount = RacingObservationLayout.WallRayCount;
            float rayMax = RacingObservationLayout.WallRayMaxMeters;
            float[] rayAngles = RacingObservationLayout.WallRayAnglesRad;
            Span<float> rayOccupancy = stackalloc float[rayCount];
            if (_collision != null)
            {
                Vector2 originXZ = new(state.Position.x, state.Position.z);
                float h = state.Heading;
                float ch = Mathf.Cos(h);
                float sh = Mathf.Sin(h);
                for (int i = 0; i < rayCount; i++)
                {
                    float a = rayAngles[i];
                    float ca = Mathf.Cos(a);
                    float sa = Mathf.Sin(a);
                    float dx = sh * ca + ch * sa;
                    float dz = ch * ca - sh * sa;
                    float d = _collision.RaycastWall(originXZ, new Vector2(dx, dz), rayMax);
                    rayOccupancy[i] = 1f - Mathf.Clamp01(d / rayMax);
                }
            }

            float surfaceCode = proj.IsOffTrack ? 1f
                : (proj.Surface == Track.SurfaceKind.Kerb ? 0.5f : 0f);

            int targetSize = _versionProfile.FloatsPerFrame;
            if (!_lastObservations.TryGetValue(carId, out var obsBuf) || obsBuf.Length != targetSize)
            {
                obsBuf = new float[targetSize];
                _lastObservations[carId] = obsBuf;
            }

            int written = RacingObservationLayout.WriteBase(
                obsBuf, state, rec.Profile.Car, proj, samples,
                yawRate, rec.Smoother.Steer, rec.Smoother.Throttle,
                rayOccupancy, surfaceCode);
            written = WriteLatestExtras(carId, rec, state, obsBuf, written);

            for (int i = 0; i < written; i++) sensor.AddObservation(obsBuf[i]);

            _agents[carId] = rec with { PrevHeading = state.Heading, HasPrevHeading = true };
        }

        private int WriteLatestExtras(CarId carId, AgentRecord rec, CarState state, float[] obsBuf, int offset)
        {
            // Per-channel observation gating. Inference scenes (no training
            // resolver) show every channel — observers/dashboards expect the
            // full vector. During training each channel is consulted via
            // IStageProfile so "what the policy sees" is declared in one
            // place, not split between this method and yaml stage_id rules.
            // Draft obs piggybacks on OpponentObservations (draft state is
            // computed from other cars' positions).
            bool showOpponents, showTires, showFuel, showDraft, showPersonality;
            if (!IsInTraining)
            {
                showOpponents = showTires = showFuel = showDraft = showPersonality = true;
            }
            else if (_active != null)
            {
                showOpponents   = _active.Has(StageFeature.OpponentObservations);
                showTires       = _active.Has(StageFeature.TireObservations);
                showFuel        = _active.Has(StageFeature.FuelObservations);
                showDraft       = _active.Has(StageFeature.OpponentObservations);
                showPersonality = _active.Has(StageFeature.PersonalityObservations);
            }
            else
            {
                // Legacy fallback for trainer scenes where the stage-profile
                // installer somehow didn't run. Mirrors the pre-refactor rules.
                int stage = _stage.Resolve();
                showOpponents   = stage >= 4;
                showTires       = stage >= 1;
                showFuel        = stage >= 2;
                showDraft       = stage >= 4;
                showPersonality = stage >= 4;
            }

            int activeCount = Mathf.Max(1, _carSim.ActiveCars.Count);
            Span<RacingObservationLayout.OtherCar> others =
                stackalloc RacingObservationLayout.OtherCar[activeCount];
            Span<Vector2> othersVel = stackalloc Vector2[activeCount];
            int n = 0;
            if (showOpponents)
            {
                foreach (var id in _carSim.ActiveCars)
                {
                    if (id.Value == carId.Value) continue;
                    if (!_carSim.TryGetState(id, out var s)) continue;
                    if (n >= others.Length) break;
                    others[n] = new RacingObservationLayout.OtherCar(id, s.Position, s.Heading, s.VelocityXZ.magnitude, s.VelocityXZ);
                    othersVel[n] = s.VelocityXZ;
                    n++;
                }
            }

            var tire = (showTires && _tires != null) ? _tires.Get(carId) : TireState.Fresh;
            var fuel = (showFuel && _fuel != null) ? _fuel.Get(carId) : FuelState.FullTank;
            var draft = (showDraft && _draft != null) ? _draft.Get(carId) : new DraftState(0f, null);
            var personality = showPersonality ? rec.Profile.Personality : DriverPersonality.Default;

            offset = RacingObservationLayout.WriteRaceContext(
                obsBuf, offset,
                selfId: carId,
                selfPos: state.Position,
                selfHeading: state.Heading,
                selfVel: state.VelocityXZ,
                maxSpeed: rec.Profile.Car.MaxSpeed,
                others: others.Slice(0, n),
                tire: tire,
                fuel: fuel,
                draft: draft,
                personality: personality);

            offset = RacingObservationLayout.WriteFrontCone(
                obsBuf, offset,
                selfPos: state.Position,
                selfHeading: state.Heading,
                selfVel: state.VelocityXZ,
                maxSpeed: rec.Profile.Car.MaxSpeed,
                others: others.Slice(0, n),
                othersVel: othersVel.Slice(0, n));

            return offset;
        }

        /// <summary>
        /// Returns the most recent observation vector written for the given
        /// car (the exact floats the policy network was fed last decision),
        /// or null if no observation has been computed for this car yet.
        /// Read-only — do not mutate.
        /// </summary>
        public IReadOnlyList<float> GetLastObservation(CarId carId)
            => _lastObservations.TryGetValue(carId, out var v) ? v : null;

        public StepResult ApplyActions(CarId carId, in ActionBuffers actions)
        {
            if (!_agents.TryGetValue(carId, out var rec)) return StepResult.None;
            var c = actions.ContinuousActions;
            float steerTgt = c.Length > 0 ? c[0] : 0f;
            float throttleTgt = c.Length > 1 ? c[1] : 0f;

            // Race-scoped resolved-driver tail: zero the policy command so a
            // finished/eliminated car coasts to a halt rather than continuing
            // to race after the lap target. The smoother still tweens —
            // sustained zeros over a few decisions decelerate gracefully.
            if (_coord != null && _coord.IsRaceScoped)
            {
                var ds = _coord.GetDriverState(carId);
                if (ds == DriverRaceState.Finished || ds == DriverRaceState.Eliminated)
                {
                    steerTgt = 0f;
                    throttleTgt = 0f;
                }
            }

            var smoother = rec.Smoother;
            var input = smoother.Step(steerTgt, throttleTgt);
            _carSim.SetInput(carId, input);
            _agents[carId] = rec with { Smoother = smoother };
            return _rewards.PostStep(carId);
        }

        public void WriteHeuristicActions(CarId carId, in ActionBuffers actionsOut)
        {
            var c = actionsOut.ContinuousActions;
            for (int i = 0; i < c.Length; i++) c[i] = 0f;
            if (!_carSim.TryGetState(carId, out var state)) return;

            float lookahead = HeuristicLookaheadCells * Track.TrackPieceConstants.CellSize;
            var input = HeuristicDriver.Compute(state, _trackQuery, lookahead, HeuristicSteerGain, HeuristicMaxThrottle);

            if (c.Length > 0) c[0] = input.Steer;
            if (c.Length > 1) c[1] = input.Throttle;
        }

        public void UnregisterAgent(CarId carId)
        {
            if (!_agents.Remove(carId)) return;
            _lastObservations.Remove(carId);
            _rewards.OnAgentUnregistered(carId);
            _carSim.Despawn(carId);
        }

        public void RegisterRewardSource(IEpisodeRewardSource source)
        {
            _rewards = source ?? NullEpisodeRewardSource.Instance;
        }

        private static float NormalizeAngle(float a)
        {
            while (a > Mathf.PI) a -= 2f * Mathf.PI;
            while (a < -Mathf.PI) a += 2f * Mathf.PI;
            return a;
        }

        private record struct AgentRecord(
            DriverProfileSnapshot Profile,
            Vector3 SpawnPosition,
            float SpawnHeading,
            System.Random Rng = null,
            float PrevHeading = 0f,
            bool HasPrevHeading = false,
            ActionSmoother Smoother = default,
            int GridSlot = 0);
    }

    /// <summary>
    /// EMA-smoothed action target. Lerps current state toward the policy's
    /// target each decision tick; the simulator interpolates between decisions
    /// via DecisionRequester.TakeActionsBetweenDecisions.
    /// </summary>
    public struct ActionSmoother
    {
        public float Steer;
        public float Throttle;

        public const float SteerSmooth = 0.5f;
        public const float ThrottleSmooth = 0.3f;

        public DriverInput Step(float steerTgt, float throttleTgt)
        {
            Steer = Mathf.Lerp(Steer, Mathf.Clamp(steerTgt, -1f, 1f), SteerSmooth);
            Throttle = Mathf.Lerp(Throttle, Mathf.Clamp(throttleTgt, -1f, 1f), ThrottleSmooth);
            return new DriverInput(Steer, Throttle, false);
        }

        public void Reset() { Steer = 0f; Throttle = 0f; }
    }
}
