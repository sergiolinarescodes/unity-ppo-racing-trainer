using System;
using UnityPpoRacingTrainer.Core.AiDriver.Physics;
using UnityPpoRacingTrainer.Core.AiDriver.Policy;
using UnityPpoRacingTrainer.Core.AiDriver.Race;
using UnityPpoRacingTrainer.Core.AiDriver.Training.Curriculum;
using UnityPpoRacingTrainer.Core.AiDriver.Training.Generation;
using UnityPpoRacingTrainer.Core.Track;
using UnityPpoRacingTrainer.Core.Track.Loop;
using Unidad.Core.EventBus;
using Unidad.Core.Grid;
using Unidad.Core.Systems;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.AiDriver.Training
{
    /// <summary>
    /// Drives the unattended training loop:
    /// <list type="bullet">
    /// <item>Builds the first loop at bootstrap so all agents have somewhere to spawn.</item>
    /// <item>Subscribes to <see cref="EpisodeEndedEvent"/>; regen the loop on Success
    /// (variety) or every <see cref="ForcedRegenEveryN"/> episodes (anti-overfit).</item>
    /// <item>Per-episode random spawn anchor + heading jitter is handled inside
    /// <see cref="AiDriverPolicyService.BeginEpisode"/> — director only owns the loop.</item>
    /// </list>
    /// </summary>
    internal sealed class TrainingDirector : SystemServiceBase
    {
        private const int MaxGeneratorRetries = 4;
        private static readonly GridPosition DefaultOrigin = new(15, 15);
        private const TrackDirection DefaultInitialFacing = TrackDirection.East;

        /// <summary>
        /// How many episodes to run before forcibly regenerating the loop,
        /// even if no Success has fired. Sized for solo-agent-per-env training
        /// — a low value rotates through the circuit pool quickly enough to
        /// prevent the policy from overfitting to a handful of layouts.
        /// UP: each env stays on the same loop for many episodes — risks
        /// over-fitting to the active layout.
        /// DOWN: regen fires constantly — full circuit-pool coverage early
        /// but more time spent paying the regen cost.
        /// </summary>
        public int ForcedRegenEveryN { get; set; } = 50;

        /// <summary>
        /// When true, ANY agent's Success terminal triggers an immediate regen.
        /// Set false for shared-loop multi-agent — one lucky completion across
        /// 64 agents should not abort the other 63 in-flight episodes.
        /// </summary>
        public bool RegenOnSuccess { get; set; } = true;

        private readonly ITrackPlacementService _placement;
        private readonly IProceduralLoopGenerator _generator;
        private readonly IClosedLoopService _loop;
        private readonly IAiDriverPolicyService _policy;
        // Optional. When non-null and IsRaceScoped, EpisodeEndedEvent stops
        // driving regen — the race coordinator's RaceEndedEvent is the only
        // boundary that fires BuildLoopForNextEpisode.
        private readonly IRaceCoordinator _coord;

        private readonly int _baseSeed;
        private int _episodeIndex;
        private int _episodesSinceRegen;
        private int _racesSinceRegen;
        private bool _initialLoopReady;

        public Vector3 CurrentSpawnPosition { get; private set; }
        public float CurrentSpawnHeading { get; private set; }
        public bool InitialLoopReady => _initialLoopReady;
        public string LastFailureReason { get; private set; }

        public TrainingDirector(
            IEventBus eventBus,
            ITrackPlacementService placement,
            IProceduralLoopGenerator generator,
            IClosedLoopService loop,
            IAiDriverPolicyService policy,
            IRaceCoordinator coord = null) : base(eventBus)
        {
            _placement = placement;
            _generator = generator;
            _loop = loop;
            _policy = policy;
            _coord = coord;
            _baseSeed = unchecked((int)(DateTime.UtcNow.Ticks & 0x7fffffff));

            Subscribe<EpisodeEndedEvent>(OnEpisodeEnded);
            Subscribe<RaceEndedEvent>(OnRaceEnded);
        }

        public void Begin()
        {
            BuildLoopForNextEpisode();
            _initialLoopReady = _loop.IsLoopClosed;
        }

        private void OnEpisodeEnded(EpisodeEndedEvent evt)
        {
            // Race-scoped mode: per-car EpisodeEndedEvent fires N times in
            // rapid succession at the race boundary. The race coordinator
            // owns the regen decision via OnRaceEnded — skip the per-car
            // path entirely so we don't regen mid-stream and tear the
            // post-race telemetry flush.
            if (_coord != null && _coord.IsRaceScoped) return;

            _episodesSinceRegen++;

            bool regen = false;
            if (RegenOnSuccess && evt.Reason == EpisodeEndReason.Success)
            {
                regen = true;
            }
            else if (ForcedRegenEveryN > 0 && _episodesSinceRegen >= ForcedRegenEveryN)
            {
                regen = true;
            }

            if (!regen) return;

            _episodesSinceRegen = 0;
            BuildLoopForNextEpisode();
        }

        // Race-scoped regen path. Fires exactly once per race (the
        // coordinator publishes RaceEndedEvent only after every driver has
        // either finished the lap target or been eliminated, or the
        // race-cap fires). RegenOnSuccess is interpreted as "regen between
        // races by default"; the abort path also forces a regen so the
        // next race opens on a fresh closed loop.
        private void OnRaceEnded(RaceEndedEvent evt)
        {
            if (_coord == null || !_coord.IsRaceScoped) return;
            _racesSinceRegen++;
            bool regen = RegenOnSuccess
                         || evt.Reason == RaceEndReason.Aborted
                         || (ForcedRegenEveryN > 0 && _racesSinceRegen >= ForcedRegenEveryN);
            if (!regen) return;
            _racesSinceRegen = 0;
            BuildLoopForNextEpisode();
        }

        private void BuildLoopForNextEpisode()
        {
            _placement.Clear();
            var stage = CurriculumStages.Default;

            for (int attempt = 0; attempt < MaxGeneratorRetries; attempt++)
            {
                int seed = MixSeed(_baseSeed, _episodeIndex, attempt);
                var cfg = new GenerationConfig(seed, DefaultOrigin, DefaultInitialFacing, stage);
                var result = _generator.Generate(cfg);
                if (result.Success && _loop.TryGetCurrentLoop(out var loop))
                {
                    LatchSpawnPose(loop);
                    // Force every registered agent to spawn at the new
                    // circuit's start line on its next OnEpisodeBegin (and
                    // teleport now so they don't ghost on the old geometry
                    // for a few ticks). Random anchor resumes after one ep.
                    _policy.RespawnAllAtStartLine(CurrentSpawnPosition, CurrentSpawnHeading);
                    LastFailureReason = null;
                    _episodeIndex++;
                    // Race-boundary signal for telemetry. Without this the
                    // per-process RaceTelemetryService keeps appending samples
                    // from cars respawned into the NEW circuit onto the OLD
                    // circuit's race record, so the viewer plots them in stale
                    // bbox coords and they "yeet" off-canvas.
                    EventBus.Publish(new CircuitRegeneratedEvent(
                        TrainingTelemetryContext.LastCircuitId ?? string.Empty,
                        loop.TotalLength,
                        loop.Anchors?.Count ?? 0));
                    // Historical fastest-lap target. Read the
                    // permanent record file and broadcast the best lap for
                    // this circuit so RewardShaper can shape pace toward
                    // (and past) it. Best=0 → unknown, shaper falls back
                    // to no record-chasing signal until the first flying
                    // lap is logged.
                    string circuitId = TrainingTelemetryContext.LastCircuitId ?? string.Empty;
                    float bestLap = CircuitRecordsStore.TryGetBestLap(circuitId);
                    EventBus.Publish(new CircuitBestLapKnownEvent(circuitId, bestLap));
                    return;
                }
                LastFailureReason = result.FailureReason;
                _placement.Clear();
            }

            Debug.LogError($"[TrainingDirector] generator failed for stage={stage.Name}, episode={_episodeIndex}, last reason: {LastFailureReason}. Reusing previous loop + spawn pose.");
            _episodeIndex++;
        }

        private void LatchSpawnPose(ClosedLoop loop)
        {
            // Read the canonical start pose from the loop itself (single
            // source of truth — same anchor + offset used by every renderer
            // and every scenario). Director never picks its own anchor.
            if (loop.Anchors == null || loop.Anchors.Count == 0)
            {
                CurrentSpawnPosition = Vector3.zero;
                CurrentSpawnHeading = 0f;
                return;
            }
            var pose = loop.GetCanonicalStartPose();
            CurrentSpawnPosition = pose.position;
            CurrentSpawnHeading = loop.GetCanonicalStartHeading();
        }

        private static int MixSeed(int baseSeed, int episode, int attempt)
        {
            unchecked
            {
                long mix = (long)baseSeed * 0x9E3779B1L + (long)episode * 0x85EBCA77L + (long)attempt * 0xC2B2AE3DL;
                return (int)(mix & 0x7FFFFFFF);
            }
        }
    }

    /// <summary>
    /// Fires after the training loop is regenerated and all registered agents
    /// have been teleported to the new start line. Telemetry consumers use
    /// this as the race boundary so each race record's samples are guaranteed
    /// to share one circuit's world coords.
    /// </summary>
    public readonly record struct CircuitRegeneratedEvent(
        string CircuitId,
        float LengthM,
        int PieceCount);
}
