using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityPpoRacingTrainer.Core.AiDriver;
using UnityPpoRacingTrainer.Core.AiDriver.Loop;
using UnityPpoRacingTrainer.Core.AiDriver.Physics;
using UnityPpoRacingTrainer.Core.AiDriver.Physics.CarCollision;
using UnityPpoRacingTrainer.Core.AiDriver.Physics.Draft;
using UnityPpoRacingTrainer.Core.AiDriver.Physics.Fuel;
using UnityPpoRacingTrainer.Core.AiDriver.Physics.Tires;
using UnityPpoRacingTrainer.Core.AiDriver.Policy;
using UnityPpoRacingTrainer.Core.AiDriver.Race;
using UnityPpoRacingTrainer.Core.AiDriver.Telemetry;
using UnityPpoRacingTrainer.Core.AiDriver.Training;
using UnityPpoRacingTrainer.Core.AiDriver.Versions;
using UnityPpoRacingTrainer.Core.Ghost.Presentation;
using UnityPpoRacingTrainer.Core.Track.Authoring.CircuitProfiles;
using Unidad.Core.EventBus;
#if AIDRIVER_TRAINING
using Unity.MLAgents;
#endif
using UnityPpoRacingTrainer.Core.Terrain;
using UnityPpoRacingTrainer.Core.Track;
using UnityPpoRacingTrainer.Core.Track.Generation.Realistic;
using UnityPpoRacingTrainer.Core.Track.Loop;
using UnityPpoRacingTrainer.Core.Track.Ribbon;
using Reflex.Core;
using Unidad.Core.Abstractions;
using Unidad.Core.Bootstrap;
using Unidad.Core.Grid;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Bootstrap
{
    /// <summary>
    /// Bootstrap for the dedicated training scene (<c>Trainer.unity</c>). Same system
    /// graph as <see cref="GameBootstrap"/> plus:
    /// <list type="bullet">
    /// <item>Eagerly initialises a flat training terrain (no player Build phase here).</item>
    /// <item>Resolves <c>TrainingDirector</c>, builds the first procedural loop, and
    /// instantiates the agent prefab(s) at the lap-start pose.</item>
    /// <item>Wires <c>TrainingDirector.StageIdProvider</c> to ML-Agents'
    /// <c>EnvironmentParameters</c> so YAML curriculum configs can flip the stage.</item>
    /// </list>
    /// Per-episode random spawn anchor + heading jitter live in
    /// <see cref="AiDriverPolicyService.BeginEpisode"/>; this bootstrap only positions
    /// agents at first instantiation.
    /// </summary>
    public sealed class TrainerBootstrap : UnidadBootstrap
    {
        [Header("Training")]
        [Tooltip("Width of the flat training terrain in cells.")]
        [SerializeField] private int terrainWidth = 30;
        [Tooltip("Depth of the flat training terrain in cells.")]
        [SerializeField] private int terrainDepth = 30;
        [Tooltip("Which AI driver model version this trainer scene runs. Latest = canonical 60-float schema with tires/fuel/draft/collision/race-state wired. Prefab, ONNX, yaml, and physics defaults all resolve from the matching IAiDriverVersionProfile via DI. Frozen snapshots get added here when new versions are taken.")]
        [SerializeField] private AiDriverVersion activeVersion = AiDriverVersion.Latest;
        [Tooltip("How many agents to spawn on the same loop. Cars ghost through each other; experience aggregates under the shared BehaviorName for ~Nx faster PPO. Hard-capped at 200 at runtime. The env var RACING_AGENT_COUNT overrides this serialized value at startup (used by the supervisor to switch counts per curriculum stage).")]
        [Range(1, 200)]
        [SerializeField] private int agentCount = 24;

        /// <summary>
        /// Returns the effective agent count, honoring the RACING_AGENT_COUNT
        /// env var if set (so the supervisor can pick 200 for warmup, 12 for
        /// grid). Falls back to the serialized field when the env var is
        /// absent or unparseable.
        /// </summary>
        private int ResolveAgentCount()
        {
            string raw = System.Environment.GetEnvironmentVariable("RACING_AGENT_COUNT");
            if (!string.IsNullOrEmpty(raw) && int.TryParse(raw, out int n) && n > 0) return n;
            return agentCount;
        }

        [Header("Editor inference overrides")]
        [Tooltip("Force a specific stage_id when running inference in the editor (no Python trainer). -1 = read from Academy / default to 0. Curriculum stages: 0=solo no-consumables, 1=solo tire, 2=solo fuel, 3=solo tire+fuel, 4=2-car draft+collision, 5=pack self-play.")]
        [Range(-1, 5)]
        [SerializeField] private int stageOverride = -1;
        [Tooltip("Force a specific circuit by its 8-char id (e.g. \"08c66d8e\"). Empty = random from the library for the active stage. Ignored on Stage 0 (recipe).")]
        [SerializeField] private string forceCircuitId = string.Empty;

#if AIDRIVER_TRAINING
        private LoopAsciiLogger _loopAsciiLogger;
#endif

        protected override void RegisterInstallers(List<ISystemInstaller> installers)
        {
            // TrainingSettings goes FIRST so every downstream service can
            // resolve ITrainingSettingsService in its ctor. Reads
            // settings.json at the project root, falls back to baked defaults
            // when missing/malformed.
            installers.Add(new UnityPpoRacingTrainer.Core.AiDriver.Config.TrainingSettingsSystemInstaller());
            installers.Add(new GridSystemInstaller());
            installers.Add(new TerrainSystemInstaller());
            // GhostPresentation registers IDropFromAirAnimator, required by
            // ValidationTrackSubInstaller (drop animation on track-piece
            // placement). Must precede TrackSystemInstaller.
            installers.Add(new GhostPresentationSystemInstaller());
            installers.Add(new TrackSystemInstaller());
            installers.Add(new RealisticTrackGenerationSystemInstaller());
            installers.Add(new AiDriverLoopSystemInstaller());
            installers.Add(new AiDriverPhysicsSystemInstaller());
            // Versions infra goes BEFORE AiDriverPolicySystemInstaller so
            // the policy service can resolve IAiDriverVersionProfile in ctor.
            installers.Add(new AiDriverVersionsSystemInstaller(activeVersion));
            // Latest requires the full tire/fuel/draft/collision/race-state
            // stack. Profile carries this via RequiresSideSystems but the
            // installer list is built before any container exists, so we read
            // a local flag here. Frozen historical snapshots that ran without
            // side systems would override this here.
            bool requiresSideSystems = true;
            if (requiresSideSystems)
            {
                installers.Add(new TirePhysicsSystemInstaller());
                installers.Add(new FuelSystemInstaller());
                installers.Add(new DraftSystemInstaller());
                installers.Add(new CarCollisionSystemInstaller());
                installers.Add(new RaceStateSystemInstaller());
                installers.Add(new CircuitTireProfileSystemInstaller());
                // Single attach-to-modifier-stack entry-point. Shared with
                // GhostDriverService (main game) so training-episode-begin
                // and ghost-spawn drive identical per-driver state.
                installers.Add(new DriverPhysicsRegistrySystemInstaller());
            }
            // Stage profile registry + IActiveStageProfile must be installed
            // BEFORE Policy + Training installers so RewardShaper and
            // AiDriverPolicyService can resolve IActiveStageProfile in their ctors.
            installers.Add(new UnityPpoRacingTrainer.Core.AiDriver.Training.Stages.StageProfileSystemInstaller());
            // Race coordinator owns the race-scoped episode lifecycle (stage
            // 5+: 1 race = 1 PPO episode for every driver, no mid-race
            // resets, race ends when all drivers either finish lap_target
            // or are eliminated). Always installed; activation reads the
            // active stage_id at race-start so stages 1–4 stay on the
            // legacy per-car episode flow.
            installers.Add(new UnityPpoRacingTrainer.Core.AiDriver.Race.RaceCoordinatorSystemInstaller());
            installers.Add(new AiDriverPolicySystemInstaller());
            installers.Add(new AiDriverTrainingSystemInstaller());
            if (requiresSideSystems)
            {
                int expectedDrivers = Mathf.Clamp(ResolveAgentCount(), 1, 200);
                // maxKept = 20: balances "small ring on disk" with the new
                // 10-min age floor (DiskJsonRaceSink.DefaultMinAgeBeforePruneSeconds)
                // so a recent race the dashboard is viewing isn't deleted out
                // from under it. With 8 envs producing ~3 races/min total,
                // ~24 races land inside the floor window — kept count needs
                // to be ≥ steady-state inflow to avoid thrashing the prune
                // path on every write.
                installers.Add(new RaceTelemetrySystemInstaller(
                    useDiskSink: true,
                    maxKept: 20,
                    expectedDriversPerRound: expectedDrivers));
            }
        }

        protected override void OnContainerReady(Container container)
        {
            Debug.Log("[TrainerBootstrap] OnContainerReady BEGIN");
            // Keep simulating when the Editor / build window loses focus.
            Application.runInBackground = true;
            container.Resolve<ITrackRibbonService>();
            container.Resolve<IClosedLoopService>();
            container.Resolve<ITrackQueryService>();
            container.Resolve<ICarSimulationService>();
            var versionProfile = container.Resolve<IAiDriverVersionProfile>();
            // Side services resolved early so subscriptions land before the
            // first agent spawns and starts publishing tick events. Only when
            // the active version requires them; stripped-down snapshots see
            // pristine CarParameters with the side-systems dormant.
            if (versionProfile.RequiresSideSystems)
            {
                container.Resolve<ITirePhysicsService>();
                container.Resolve<IFuelService>();
                container.Resolve<IDraftService>();
                container.Resolve<ICarCollisionService>();
                container.Resolve<IRaceStateService>();
                container.Resolve<ICircuitTireProfileService>();
                // RaceCoordinator before RaceTelemetryRecorder so its
                // RaceStarted/RaceEnded subscriptions are alive before the
                // telemetry recorder forwards them to the on-disk sink.
                container.Resolve<IRaceCoordinator>();
                container.Resolve<IRaceTelemetryRecorder>();
            }
            var policy = container.Resolve<IAiDriverPolicyService>();
            // Sandbox: pin spawn to longest-straight midpoint. Value function
            // can't fit returns when every episode begins from a different
            // anchor + heading on a regenerating loop. Re-enable random per-
            // section exposure after stage 0 if needed.
            if (policy is AiDriverPolicyService concretePolicy)
            {
                concretePolicy.Spawn = SpawnStrategy.LongestStraightMidpoint;
            }
            Debug.Log("[TrainerBootstrap] core services resolved");

#if AIDRIVER_TRAINING
            var rewardSource = container.Resolve<IEpisodeRewardSource>();
            policy.RegisterRewardSource(rewardSource);

            var terrain = container.Resolve<ITerrainService>();
            terrain.Initialize(new TerrainBuildOptions(terrainWidth, terrainDepth, 0, TrackPieceConstants.CellSize));
            Debug.Log($"[TrainerBootstrap] terrain initialized {terrainWidth}x{terrainDepth}");

            var loop = container.Resolve<IClosedLoopService>();
            var director = container.Resolve<TrainingDirector>();
            director.StageIdProvider = ResolveStageIdFromAcademy;
            container.Resolve<IStageIdProvider>().Resolver = ResolveStageIdFromAcademy;

            // Editor inference override: pin the selector to a specific
            // circuit id so you can rerun the same loop deterministically
            // for visual debugging. Disables the director's regen triggers
            // (per-Success + every-N) so the pinned circuit survives every
            // episode end — otherwise the director rebuilds the loop on the
            // next OnEpisodeEnded and the selector replays the same id but
            // re-clears placement, flickering the scene unnecessarily.
            bool circuitPinned = !string.IsNullOrWhiteSpace(forceCircuitId);
            if (circuitPinned)
            {
                var selector = container.Resolve<AiDriver.Training.Generation.CurriculumGeneratorSelector>();
                selector.ForcedCircuitId = forceCircuitId.Trim();
                Debug.Log($"[TrainerBootstrap] forceCircuitId='{selector.ForcedCircuitId}'");
            }

            // ASCII dump of every closed loop → results/loop_dumps.log.
            // Tail with: Get-Content -Wait results/loop_dumps.log
            _loopAsciiLogger = new LoopAsciiLogger(
                container.Resolve<IEventBus>(),
                container.Resolve<ITrackPlacementService>(),
                loop, ResolveStageIdFromAcademy);

            // Multi-agent shared loop: one Success across N agents should not
            // abort the in-flight episodes of the other N-1. Forced cadence
            // takes over.
            // Hard cap 200: race telemetry treats a "full race" as all agents
            // ending at least once, and 200 keeps that round bounded.
            int n = Mathf.Clamp(ResolveAgentCount(), 1, 200);
            if (n > 1) director.RegenOnSuccess = false;

            // Pinned-circuit visual debug: kill BOTH regen triggers so the
            // selected circuit persists across every Success and every
            // ForcedRegenEveryN window. Active for any agent count.
            if (circuitPinned)
            {
                director.RegenOnSuccess = false;
                director.ForcedRegenEveryN = 0;
                Debug.Log("[TrainerBootstrap] regen disabled (RegenOnSuccess=false, ForcedRegenEveryN=0) for pinned circuit.");
            }

            // All stages now train on authored-closure circuits, which always
            // include the production wall geometry; no wall-free warmup stage.
            int stageNow = ResolveStageIdFromAcademy();
            Track.TrackPlacementService.EmitWalls = true;
            Debug.Log($"[TrainerBootstrap] stage={stageNow} EmitWalls=true (authored library)");

            // Open the telemetry sink early so episode end + circuit change
            // events are captured from the very first episode.
            TrainingTelemetry.EnsureOpen();

            director.Begin();
            Debug.Log($"[TrainerBootstrap] director.Begin done; loopReady={director.InitialLoopReady} stage={director.LastStageId} spawn={director.CurrentSpawnPosition}");

            if (!director.InitialLoopReady)
            {
                Debug.LogError("[TrainerBootstrap] initial loop did not close — agent will spawn at the fallback pose.");
            }
            FrameIsoCamera(loop);

            IAiDriverAgentRef firstAgent = null;
            for (int i = 0; i < n; i++)
            {
                var agent = SpawnAgent(versionProfile, director.CurrentSpawnPosition, director.CurrentSpawnHeading, i);
                if (i == 0) firstAgent = agent;
            }
            Debug.Log($"[TrainerBootstrap] spawned {n} agents on stage {director.LastStageId}");

            // Memory probe: periodic snapshot to results/_telemetry/mem_<pid>_<stamp>.jsonl.
            // Reveals which subsystem grows over time when the trainer leaks RAM
            // — process Working/Private, GC, Unity profiler counters (mesh /
            // texture / native / total reserved), live placement count, and
            // in-flight race telemetry sizes. Tail with
            //   Get-Content -Wait results/_telemetry/mem_*.jsonl
            var memProbe = gameObject.AddComponent<MemoryProbe>();
            memProbe.Bind(
                versionProfile.RequiresSideSystems
                    ? container.Resolve<IRaceTelemetryRecorder>()
                    : null,
                container.Resolve<ITrackPlacementService>());

            if (firstAgent != null)
            {
                var hud = gameObject.AddComponent<TrainerHud>();
                hud.Bind(container.Resolve<IEventBus>(),
                    container.Resolve<ICarSimulationService>(),
                    loop, director, firstAgent);

                var modelHud = gameObject.AddComponent<LoadedModelHud>();
                modelHud.Bind(versionProfile, activeVersion);

                // Only attach the visual debug renderer in editor — headless
                // build lacks the Sprites/Default + URP shaders so material
                // construction throws every frame and pollutes the log.
                if (Application.isEditor)
                {
                    var dbg = gameObject.AddComponent<LookaheadDebugRenderer>();
                    dbg.Bind(firstAgent, loop,
                        container.Resolve<ITrackQueryService>(),
                        container.Resolve<ICarSimulationService>());

                    // Wall feeler-ray viz mirrors the obs layout — same 6 angles.
                    // Yellow = clear, red = wall hit. Lets you eyeball whether
                    // the policy "sees" the upcoming barrier before it arrives.
                    var rayDbg = gameObject.AddComponent<WallRayDebugRenderer>();
                    rayDbg.Bind(firstAgent,
                        container.Resolve<ICarSimulationService>(),
                        TryResolveCollision(container));

                    // K=9 micro-sector boundaries + start/finish line. Colored
                    // posts (red/green/cyan per macro) make sector-checkpoint
                    // lap counting auditable at a glance — if the agent's
                    // currentMicro telemetry doesn't match the post it's
                    // currently between, the sector math is wrong.
                    SectorBoundaryDebugRenderer.MountOn(gameObject, loop);
                }
            }
#else
            Debug.LogError("[TrainerBootstrap] AIDRIVER_TRAINING is not defined — the trainer scene cannot drive PPO without it.");
#endif
        }

        private static ITrackCollisionService TryResolveCollision(Container container)
        {
            try { return container.Resolve<ITrackCollisionService>(); }
            catch { return null; }
        }

#if AIDRIVER_TRAINING
        private int ResolveStageIdFromAcademy()
        {
            // Curriculum spans stage 0..5 (Stage0SoloWarmup .. Stage5PackSelfPlay).
            if (stageOverride >= 0) return Mathf.Clamp(stageOverride, 0, 5);
            float v = Academy.Instance.EnvironmentParameters.GetWithDefault("stage_id", 0f);
            return Mathf.Clamp(Mathf.RoundToInt(v), 0, 5);
        }
#endif

        // Captured by SpawnAgent so the LoadedModelHud can display what's
        // actually wired without having to re-resolve the sentinel.
        internal static string LastResolvedModelPath;
        internal static string LastResolvedModelAssetName;

        private static string ResolveOnnxResourcePath(string profilePath)
        {
            if (string.IsNullOrEmpty(profilePath)) return null;
            if (profilePath != "latest") return profilePath;
#if UNITY_EDITOR
            string dir = System.IO.Path.Combine(Application.dataPath, "Resources/AiDriver/Policies");
            if (!System.IO.Directory.Exists(dir)) return null;
            // Highest-version wins; mtime is the tiebreak between equal versions.
            // Avoids the foot-gun where `git checkout` / re-import bumps an older
            // checkpoint's mtime above the latest premium model.
            System.IO.FileInfo best = null;
            (int major, int minor) bestVer = (-1, -1);
            foreach (var path in System.IO.Directory.GetFiles(dir, "RacingDriver-*.onnx"))
            {
                var fi = new System.IO.FileInfo(path);
                var ver = ParseVersionSuffix(fi.Name);
                if (best == null
                    || ver.major > bestVer.major
                    || (ver.major == bestVer.major && ver.minor > bestVer.minor)
                    || (ver.major == bestVer.major && ver.minor == bestVer.minor
                        && fi.LastWriteTimeUtc > best.LastWriteTimeUtc))
                {
                    best = fi;
                    bestVer = ver;
                }
            }
            if (best == null)
            {
                Debug.LogWarning("[TrainerBootstrap] OnnxResourcePath=latest but no RacingDriver-*.onnx in Resources/AiDriver/Policies/.");
                return null;
            }
            return "AiDriver/Policies/" + System.IO.Path.GetFileNameWithoutExtension(best.Name);
#else
            // Built player has no filesystem visibility into Resources — caller
            // will fall back to the prefab's serialized m_Model.
            return null;
#endif
        }

        // RacingDriver-v3.4-cold-6599k.onnx → (3, 4)
        // RacingDriver-v2-fix-foo-112599646.onnx → (2, 0)
        private static (int major, int minor) ParseVersionSuffix(string fileName)
        {
            const string prefix = "RacingDriver-v";
            if (!fileName.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase)) return (-1, -1);
            int i = prefix.Length;
            int majorStart = i;
            while (i < fileName.Length && char.IsDigit(fileName[i])) i++;
            if (i == majorStart) return (-1, -1);
            int major = int.Parse(fileName.Substring(majorStart, i - majorStart));
            int minor = 0;
            if (i < fileName.Length && fileName[i] == '.')
            {
                int minorStart = ++i;
                while (i < fileName.Length && char.IsDigit(fileName[i])) i++;
                if (i > minorStart) minor = int.Parse(fileName.Substring(minorStart, i - minorStart));
            }
            return (major, minor);
        }

        private IAiDriverAgentRef SpawnAgent(IAiDriverVersionProfile profile, Vector3 spawnPos, float heading, int gridSlot)
        {
            var prefab = Resources.Load<GameObject>(profile.PrefabResourcePath);
            if (prefab == null)
            {
                Debug.LogError($"[TrainerBootstrap] AiDriverAgent prefab missing at Resources/{profile.PrefabResourcePath}.prefab (version={profile.Version}).");
                return null;
            }

            // Deactivate the prefab BEFORE Instantiate so the clone wakes up
            // inactive — otherwise Agent.Awake/OnEnable runs on Instantiate
            // and builds the VectorSensor from the prefab's serialized
            // VectorObservationSize before our profile patch lands. Restore
            // the source asset's active state immediately so we don't ship a
            // dirty prefab. Mirrors RealisticLoopInferenceScenario.
            bool wasActive = prefab.activeSelf;
            if (wasActive) prefab.SetActive(false);
            var go = Instantiate(prefab);
            if (wasActive) prefab.SetActive(true);

            var agentRef = go.GetComponent<IAiDriverAgentRef>();
            if (agentRef == null)
            {
                Debug.LogError($"[TrainerBootstrap] Prefab Resources/{profile.PrefabResourcePath} has no IAiDriverAgentRef component (no AiDriverAgentBehaviour attached).");
                Destroy(go);
                return null;
            }

            // Patch BehaviorParameters from the version profile so the prefab
            // doesn't need a brain-params bump every time the canonical
            // observation schema changes. The serialized prefab values become
            // a don't-care; the profile is the single source of truth. Must
            // run BEFORE SetActive(true) — Agent.OnEnable registers the
            // behavior with the Academy using whatever's set here.
            var brain = go.GetComponent<Unity.MLAgents.Policies.BehaviorParameters>();
            if (brain != null)
            {
                brain.BrainParameters.VectorObservationSize = profile.FloatsPerFrame;
                brain.BrainParameters.NumStackedVectorObservations = 3;
                brain.BrainParameters.ActionSpec = Unity.MLAgents.Actuators.ActionSpec.MakeContinuous(2);
                brain.BehaviorName = profile.BehaviorName;

                // Resolve + assign the ONNX from the profile. Sentinel "latest"
                // → newest-mtime RacingDriver-*.onnx under Resources/AiDriver/
                // Policies (Editor only; built players fall back to prefab
                // m_Model). This lets the user press Play on TrainerTest after
                // a fresh train without touching the prefab.
                string resolvedPath = ResolveOnnxResourcePath(profile.OnnxResourcePath);
                if (!string.IsNullOrEmpty(resolvedPath))
                {
                    var model = Resources.Load<Unity.InferenceEngine.ModelAsset>(resolvedPath);
                    if (model != null)
                    {
                        brain.Model = model;
                        LastResolvedModelPath = resolvedPath;
                        LastResolvedModelAssetName = model.name;
                    }
                    else
                    {
                        Debug.LogError($"[TrainerBootstrap] ONNX missing at Resources/{resolvedPath}.onnx — falling back to prefab m_Model.");
                    }
                }
            }
            else
            {
                Debug.LogError($"[TrainerBootstrap] Prefab Resources/{profile.PrefabResourcePath} has no BehaviorParameters component.");
            }

            // Latest keeps the staggered grid for car-car race. Frozen
            // historical snapshots that ran on a single stacked pose would
            // force slot 0 by setting profile.RequiresSideSystems = false.
            int slot = profile.RequiresSideSystems ? gridSlot : 0;
            ConfigureAgent(agentRef, spawnPos, heading, slot);
            go.AddComponent<AiDriverAgentVisualizer>();
            go.SetActive(true);
            return agentRef;
        }

        private static void ConfigureAgent(IAiDriverAgentRef agentRef, Vector3 spawnPos, float heading, int slot)
        {
            // Configure() is a non-interface method present on every Behaviour
            // shell — cast to the concrete to call it. The shells share the
            // signature by convention (enforced by VersionedSpawnScenario).
            switch (agentRef)
            {
                case AiDriverAgentBehaviour latest:
                    latest.Configure("default", spawnPos, heading, slot);
                    break;
                default:
                    Debug.LogError($"[TrainerBootstrap] Unknown IAiDriverAgentRef type {agentRef.GetType().FullName} — cannot Configure spawn pose.");
                    break;
            }
        }

        private void FrameIsoCamera(IClosedLoopService loop)
        {
            var cam = Camera.main;
            if (cam == null) return;
            if (loop != null && loop.TryGetCurrentLoop(out var l))
            {
                var target = l.LapStartPosition;
                cam.transform.position = target + new Vector3(20f, 25f, -20f);
                cam.transform.LookAt(target);
            }
        }

        // Resolve every ITickable / IFixedTickable the container knows about so
        // the framework's TickRunner drives them each frame / fixed step. The
        // base implementations return empty lists, which would silently leave
        // RaceCoordinator.Tick uncalled — stage 5+ race-scoped episodes depend
        // on the coordinator's MaxRaceSteps + PostFinishBudget countdowns to
        // backstop stuck races, so missing the override deadlocks the run.
        protected override List<ITickable> ResolveTickables(Container container)
        {
            return new List<ITickable>(container.All<ITickable>());
        }

        protected override List<IFixedTickable> ResolveFixedTickables(Container container)
        {
            return new List<IFixedTickable>(container.All<IFixedTickable>());
        }

#if AIDRIVER_TRAINING
        // Subscribes to LoopClosedEvent and dumps an ASCII map of every
        // closed track to results/loop_dumps.log. Tail with:
        //   Get-Content -Wait results/loop_dumps.log
        private sealed class LoopAsciiLogger : IDisposable
        {
            private const string LogPath = "results/loop_dumps.log";
            // Cap so an overnight unattended run can't accumulate a 100k-line
            // log. First N closures are usually all you need to verify the
            // generator + closure stack is healthy; beyond that, silent.
            private const int MaxLoggedClosures = 200;
            private readonly ITrackPlacementService _placement;
            private readonly IClosedLoopService _loop;
            private readonly IDisposable _subscription;
            private readonly Func<int> _stageIdProvider;
            private int _closures;

            public LoopAsciiLogger(IEventBus bus, ITrackPlacementService placement,
                IClosedLoopService loop, Func<int> stageIdProvider)
            {
                _placement = placement;
                _loop = loop;
                _stageIdProvider = stageIdProvider ?? (() => -1);
                _subscription = bus.Subscribe<LoopClosedEvent>(OnLoopClosed);
                try
                {
                    Directory.CreateDirectory("results");
                    File.WriteAllText(LogPath, $"# loop_dumps.log opened {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
                }
                catch (Exception e) { Debug.LogWarning($"[LoopAsciiLogger] open failed: {e.Message}"); }
            }

            public void Dispose() => _subscription?.Dispose();

            private void OnLoopClosed(LoopClosedEvent evt)
            {
                _closures++;
                if (_closures > MaxLoggedClosures) return;
                try { File.AppendAllText(LogPath, Render(evt)); }
                catch (Exception e) { Debug.LogWarning($"[LoopAsciiLogger] write failed: {e.Message}"); }
            }

            private string Render(LoopClosedEvent evt)
            {
                var sb = new StringBuilder(2048);
                sb.Append("=== closure #").Append(_closures)
                  .Append("  stage=").Append(_stageIdProvider())
                  .Append("  loopId=").Append(evt.LoopId)
                  .Append("  pieces=").Append(_placement.Placed.Count)
                  .Append("  anchors=").Append(evt.AnchorCount)
                  .Append("  totalLen=").Append(evt.TotalLength.ToString("F1"))
                  .Append('\n');

                if (_placement.Occupancy.Count == 0)
                {
                    sb.Append("(no occupancy)\n\n");
                    return sb.ToString();
                }

                int minX = int.MaxValue, maxX = int.MinValue;
                int minZ = int.MaxValue, maxZ = int.MinValue;
                foreach (var kv in _placement.Occupancy)
                {
                    if (kv.Key.X < minX) minX = kv.Key.X;
                    if (kv.Key.X > maxX) maxX = kv.Key.X;
                    if (kv.Key.Y < minZ) minZ = kv.Key.Y;
                    if (kv.Key.Y > maxZ) maxZ = kv.Key.Y;
                }
                minX -= 1; minZ -= 1; maxX += 1; maxZ += 1;
                int w = maxX - minX + 1, h = maxZ - minZ + 1;
                var grid = new char[h, w];
                for (int z = 0; z < h; z++)
                    for (int x = 0; x < w; x++)
                        grid[z, x] = '.';
                foreach (var kv in _placement.Occupancy)
                    grid[kv.Key.Y - minZ, kv.Key.X - minX] = '#';

                if (_loop.TryGetCurrentLoop(out var closed))
                {
                    float cell = TrackPieceConstants.CellSize;
                    int sx = Mathf.RoundToInt(closed.LapStartPosition.x / cell);
                    int sz = Mathf.RoundToInt(closed.LapStartPosition.z / cell);
                    int rx = sx - minX, rz = sz - minZ;
                    if (rx >= 0 && rx < w && rz >= 0 && rz < h)
                        grid[rz, rx] = 'S';
                }

                for (int z = h - 1; z >= 0; z--)
                {
                    for (int x = 0; x < w; x++) sb.Append(grid[z, x]);
                    sb.Append('\n');
                }
                sb.Append('\n');
                return sb.ToString();
            }
        }
#endif
    }

    /// <summary>
    /// In-scene visual debug for the AI driver's curvature lookahead. Draws a
    /// LineRenderer from the FIRST agent to each of the 5 lookahead anchor
    /// positions, plus a colored sphere at each anchor:
    /// <list type="bullet">
    /// <item><b>red</b> intensity ∝ |κ_norm|, hue tilts red on right curves</item>
    /// <item><b>blue</b> on left curves</item>
    /// <item><b>green</b> on near-straight (|κ| &lt; 0.1)</item>
    /// </list>
    /// If lookahead samples bunch up at the same point or curvature signs are
    /// wrong, you'll see it immediately. Co-located here to dodge Unity's
    /// new-file refresh cost.
    /// </summary>
    internal sealed class LookaheadDebugRenderer : MonoBehaviour
    {
        private const int N = 5;

        private IAiDriverAgentRef _agent;
        private IClosedLoopService _loop;
        private ITrackQueryService _trackQuery;
        private ICarSimulationService _carSim;

        private LineRenderer[] _lines;
        private GameObject[] _markers;
        private Material[] _markerMats;

        public void Bind(IAiDriverAgentRef agent, IClosedLoopService loop,
                         ITrackQueryService trackQuery, ICarSimulationService carSim)
        {
            _agent = agent;
            _loop = loop;
            _trackQuery = trackQuery;
            _carSim = carSim;
        }

        private void Start()
        {
            _lines = new LineRenderer[N];
            _markers = new GameObject[N];
            _markerMats = new Material[N];

            var shader = Shader.Find("Sprites/Default");
            for (int i = 0; i < N; i++)
            {
                var lineGo = new GameObject($"LookaheadLine{i}");
                lineGo.transform.SetParent(transform, false);
                var lr = lineGo.AddComponent<LineRenderer>();
                lr.startWidth = 0.08f;
                lr.endWidth = 0.08f;
                lr.material = new Material(shader);
                lr.positionCount = 2;
                lr.useWorldSpace = true;
                _lines[i] = lr;

                var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                marker.name = $"LookaheadMarker{i}";
                marker.transform.SetParent(transform, false);
                marker.transform.localScale = Vector3.one * (0.25f + 0.08f * i);
                Destroy(marker.GetComponent<Collider>());
                var mat = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));
                marker.GetComponent<MeshRenderer>().sharedMaterial = mat;
                _markers[i] = marker;
                _markerMats[i] = mat;
            }
        }

        private void Update()
        {
            if (_agent == null || !_agent.IsRegistered) { Hide(); return; }
            if (!_carSim.TryGetState(_agent.CarId, out var state)) { Hide(); return; }
            if (!_trackQuery.HasLoop || !_loop.TryGetCurrentLoop(out var closed)) { Hide(); return; }

            var proj = _trackQuery.Project(state.Position, state.LastAnchorIndex);

            // AiDriverPhysicsDefaults.Latest is scheduled for deletion (Step 5). Until
            // then, this debug renderer reads the canonical default — it
            // matches the Latest profile's PhysicsDefaults today.
            float maxSpeed = AiDriverPhysicsDefaults.Latest.MaxSpeed;
            float capMeters = closed.TotalLength > 0f ? closed.TotalLength * 0.5f : float.MaxValue;
            Span<float> offsets = stackalloc float[N];
            for (int i = 0; i < N; i++)
                offsets[i] = Mathf.Min(RacingObservationLayout.LookaheadSeconds[i] * maxSpeed, capMeters);

            Span<CenterlineSample> samples = stackalloc CenterlineSample[N];
            _trackQuery.SampleLookaheadAt(proj.NearestAnchorIndex, offsets, samples);

            float kappaScale = (maxSpeed * maxSpeed) / 8f;
            Vector3 carPos = state.Position + Vector3.up * 0.4f;
            for (int i = 0; i < N; i++)
            {
                var s = samples[i];
                float kappa = s.Curvature;
                float kappaNorm = Mathf.Clamp(kappa * kappaScale, -1f, 1f);
                float mag = Mathf.Abs(kappaNorm);
                Color color;
                if (kappa > 0.1f / kappaScale) color = Color.Lerp(Color.green, Color.red, mag);
                else if (kappa < -0.1f / kappaScale) color = Color.Lerp(Color.green, Color.blue, mag);
                else color = Color.green;

                Vector3 worldPos = s.Position + Vector3.up * 0.4f;
                _lines[i].enabled = true;
                _lines[i].SetPosition(0, carPos);
                _lines[i].SetPosition(1, worldPos);
                _lines[i].startColor = color;
                _lines[i].endColor = color;

                _markers[i].SetActive(true);
                _markers[i].transform.position = worldPos;
                _markerMats[i].color = color;
            }
        }

        private void Hide()
        {
            if (_lines == null) return;
            for (int i = 0; i < N; i++)
            {
                if (_lines[i] != null) _lines[i].enabled = false;
                if (_markers[i] != null) _markers[i].SetActive(false);
            }
        }
    }

    /// <summary>
    /// Small top-left OnGUI panel listing what the agent is actually running:
    /// active version, behavior name, observation float-count, ONNX file name,
    /// and its filesystem mtime. Reads <see cref="TrainerBootstrap.LastResolvedModelPath"/>
    /// captured during <c>SpawnAgent</c>; falls back to the prefab's serialized
    /// model name if the sentinel resolved to null. Editor + dev-build only —
    /// shows nothing in shipping player builds.
    /// </summary>
    internal sealed class LoadedModelHud : MonoBehaviour
    {
        private IAiDriverVersionProfile _profile;
        private AiDriverVersion _activeVersion;
        private string _modelFileLabel = "(unresolved)";
        private string _modelMtimeLabel = "";
        private GUIStyle _style;

        public void Bind(IAiDriverVersionProfile profile, AiDriverVersion activeVersion)
        {
            _profile = profile;
            _activeVersion = activeVersion;
            RefreshLabels();
        }

        private void RefreshLabels()
        {
            string path = TrainerBootstrap.LastResolvedModelPath;
            if (string.IsNullOrEmpty(path))
            {
                _modelFileLabel = string.IsNullOrEmpty(TrainerBootstrap.LastResolvedModelAssetName)
                    ? "prefab m_Model (sentinel unresolved)"
                    : TrainerBootstrap.LastResolvedModelAssetName + " (prefab fallback)";
                _modelMtimeLabel = "";
                return;
            }
            _modelFileLabel = System.IO.Path.GetFileName(path) + ".onnx";
#if UNITY_EDITOR
            try
            {
                string abs = System.IO.Path.Combine(Application.dataPath, "Resources/" + path + ".onnx");
                if (System.IO.File.Exists(abs))
                {
                    var fi = new System.IO.FileInfo(abs);
                    _modelMtimeLabel = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm");
                }
            }
            catch { _modelMtimeLabel = ""; }
#endif
        }

        private void OnGUI()
        {
            if (_profile == null) return;
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.box)
                {
                    alignment = TextAnchor.UpperLeft,
                    fontSize = 12,
                    padding = new RectOffset(8, 8, 6, 6),
                    richText = true,
                };
                _style.normal.textColor = Color.white;
            }
            string text =
                $"<b>AI Driver — {_activeVersion}</b>\n" +
                $"behavior: {_profile.BehaviorName}\n" +
                $"obs/frame: {_profile.FloatsPerFrame}\n" +
                $"model: {_modelFileLabel}";
            if (!string.IsNullOrEmpty(_modelMtimeLabel))
                text += $"\nmtime: {_modelMtimeLabel}";
            var content = new GUIContent(text);
            var size = _style.CalcSize(content);
            GUI.Box(new Rect(10, 10, Mathf.Max(220, size.x + 8), size.y + 8), content, _style);
        }
    }

    /// <summary>
    /// Mirrors the wall-distance feeler rays the policy observation samples
    /// every decision tick. Six rays at body-relative
    /// {-90°, -45°, -22.5°, +22.5°, +45°, +90°} from the car's heading,
    /// drawn as <see cref="LineRenderer"/>s in world space. Color encodes
    /// occupancy: bright yellow when clear, lerping to red as a wall closes
    /// in (occupancy = 1 - dist/RayMaxMeters).
    /// Co-located with <see cref="LookaheadDebugRenderer"/> for the same
    /// new-file-csproj-thrash reason.
    /// </summary>
    internal sealed class WallRayDebugRenderer : MonoBehaviour
    {
        private IAiDriverAgentRef _agent;
        private ICarSimulationService _carSim;
        private ITrackCollisionService _collision;

        private LineRenderer[] _lines;

        public void Bind(IAiDriverAgentRef agent, ICarSimulationService carSim,
            ITrackCollisionService collision)
        {
            _agent = agent;
            _carSim = carSim;
            _collision = collision;
        }

        private void Start()
        {
            int n = RacingObservationLayout.WallRayCount;
            _lines = new LineRenderer[n];
            var shader = Shader.Find("Sprites/Default");
            for (int i = 0; i < n; i++)
            {
                var go = new GameObject($"WallRay{i}");
                go.transform.SetParent(transform, false);
                var lr = go.AddComponent<LineRenderer>();
                lr.startWidth = 0.04f;
                lr.endWidth = 0.04f;
                lr.material = new Material(shader);
                lr.positionCount = 2;
                lr.useWorldSpace = true;
                _lines[i] = lr;
            }
        }

        private void Update()
        {
            if (_lines == null) return;
            if (_agent == null || !_agent.IsRegistered || _carSim == null || _collision == null
                || !_carSim.TryGetState(_agent.CarId, out var state))
            {
                for (int i = 0; i < _lines.Length; i++)
                    if (_lines[i] != null) _lines[i].enabled = false;
                return;
            }

            float h = state.Heading;
            float ch = Mathf.Cos(h);
            float sh = Mathf.Sin(h);
            Vector2 origin = new(state.Position.x, state.Position.z);
            float maxR = RacingObservationLayout.WallRayMaxMeters;
            Vector3 originW = state.Position + Vector3.up * 0.45f;

            for (int i = 0; i < _lines.Length; i++)
            {
                float a = RacingObservationLayout.WallRayAnglesRad[i];
                float ca = Mathf.Cos(a);
                float sa = Mathf.Sin(a);
                float dx = sh * ca + ch * sa;
                float dz = ch * ca - sh * sa;
                float d = _collision.RaycastWall(origin, new Vector2(dx, dz), maxR);
                Vector3 endW = originW + new Vector3(dx, 0f, dz) * d;
                float occ = 1f - Mathf.Clamp01(d / maxR);
                Color c = Color.Lerp(Color.yellow, Color.red, occ);

                _lines[i].enabled = true;
                _lines[i].SetPosition(0, originW);
                _lines[i].SetPosition(1, endW);
                _lines[i].startColor = c;
                _lines[i].endColor = c;
            }
        }
    }

    /// <summary>
    /// Periodic memory snapshot for the headless trainer. Writes one JSONL
    /// line every <c>SamplePeriodSeconds</c> to
    /// <c>results/_telemetry/mem_&lt;pid&gt;_&lt;stamp&gt;.jsonl</c> capturing:
    /// process Working/Private (Windows view of total RAM use), GC managed
    /// heap, Unity Profiler recorders for native / mesh / texture / total-
    /// reserved memory, plus live counts from the placement service and the
    /// in-flight race-telemetry buffer. Plotting any of these against wall-
    /// clock reveals which counter climbs linearly when the trainer leaks;
    /// without this probe every fix is a guess. Co-located here to dodge the
    /// new-.cs-file csproj-refresh dance per <c>feedback_unity_new_cs_files.md</c>.
    /// </summary>
    internal sealed class MemoryProbe : MonoBehaviour
    {
        private const float SamplePeriodSeconds = 30f;

        private IRaceTelemetryRecorder _telemetry;
        private ITrackPlacementService _placement;
        private System.IO.StreamWriter _writer;
        private string _filePath;
        private float _accum;
        private bool _disabled;

        // Using UnityEngine.Profiling.Profiler static methods instead of
        // Unity.Profiling.Recorder — the Recorder API lives in a separate
        // asmdef (Unity.Profiling.Core) we'd have to wire in, while the
        // static Profiler methods are always available via UnityEngine.

        public void Bind(IRaceTelemetryRecorder telemetry, ITrackPlacementService placement)
        {
            _telemetry = telemetry;
            _placement = placement;
        }

        private void Start()
        {
            // LogError (not LogWarning) so the trainer's unbuffered stderr
            // stream surfaces this. Without it, headless Unity buffers
            // stdout/Player log lines and we can't tell whether Start ran.
            Debug.LogError("[MemoryProbe] Start invoked");
            try
            {
                string projectRoot = ResolveProjectRoot();
                if (projectRoot == null)
                {
                    Debug.LogError("[MemoryProbe] disabled — ResolveProjectRoot returned null");
                    _disabled = true;
                    return;
                }
                string dir = System.IO.Path.Combine(projectRoot, "results", "_telemetry");
                System.IO.Directory.CreateDirectory(dir);
                int pid;
                try { pid = System.Diagnostics.Process.GetCurrentProcess().Id; }
                catch { pid = 0; }
                string stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                _filePath = System.IO.Path.Combine(dir, $"mem_{pid}_{stamp}.jsonl");
                _writer = new System.IO.StreamWriter(
                    new System.IO.FileStream(_filePath,
                        System.IO.FileMode.Create,
                        System.IO.FileAccess.Write,
                        System.IO.FileShare.Read))
                { AutoFlush = true };
                Debug.LogError($"[MemoryProbe] opened {_filePath}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[MemoryProbe] disabled — open failed: {e.GetType().Name}: {e.Message}");
                _disabled = true;
                return;
            }
            // Emit one immediate sample so the first line lands at process
            // start, not 30 s in.
            WriteSample();
        }

        private void Update()
        {
            if (_disabled || _writer == null) return;
            _accum += Time.unscaledDeltaTime;
            if (_accum < SamplePeriodSeconds) return;
            _accum = 0f;
            WriteSample();
        }

        private void OnDestroy()
        {
            try { _writer?.Flush(); _writer?.Dispose(); }
            catch { }
            _writer = null;
        }

        private void WriteSample()
        {
            try
            {
                var sb = new System.Text.StringBuilder(640);
                sb.Append("{\"event\":\"mem\"");
                sb.Append(",\"ts\":\""); sb.Append(DateTime.UtcNow.ToString("o")); sb.Append('"');

                // Process-level (Windows view).
                long ws = 0L, priv = 0L;
                try
                {
                    var p = System.Diagnostics.Process.GetCurrentProcess();
                    ws = p.WorkingSet64;
                    priv = p.PrivateMemorySize64;
                }
                catch { }
                sb.Append(",\"ws_mb\":");      sb.Append(ws / 1048576L);
                sb.Append(",\"private_mb\":"); sb.Append(priv / 1048576L);

                // Managed GC heap.
                sb.Append(",\"gc_alloc_mb\":"); sb.Append(GC.GetTotalMemory(forceFullCollection: false) / 1048576L);

                // Unity-side reserves. Profiler static methods return 0 if
                // Profiler is disabled in the build, but the trainer build
                // keeps Development+Profiler symbols enabled.
                long uReserved = 0L, uAllocated = 0L, uMonoHeap = 0L, uMonoUsed = 0L, uTempAlloc = 0L;
                try { uReserved   = UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong(); } catch { }
                try { uAllocated  = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong(); } catch { }
                try { uMonoHeap   = UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong(); } catch { }
                try { uMonoUsed   = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong(); } catch { }
                try { uTempAlloc  = UnityEngine.Profiling.Profiler.GetTempAllocatorSize(); } catch { }
                sb.Append(",\"u_total_reserved_mb\":");  sb.Append(uReserved   / 1048576L);
                sb.Append(",\"u_total_allocated_mb\":"); sb.Append(uAllocated  / 1048576L);
                sb.Append(",\"u_mono_heap_mb\":");       sb.Append(uMonoHeap   / 1048576L);
                sb.Append(",\"u_mono_used_mb\":");       sb.Append(uMonoUsed   / 1048576L);
                sb.Append(",\"u_temp_alloc_mb\":");      sb.Append(uTempAlloc  / 1048576L);

                // Subsystem counts. Any of these climbing linearly with
                // wall clock = the leak.
                int placed = 0;
                try { if (_placement != null) placed = _placement.Placed.Count; } catch { }
                sb.Append(",\"placed_count\":"); sb.Append(placed);

                int driverCount = 0, sampleSum = 0, eventCount = 0;
                try
                {
                    var inFlight = _telemetry?.CurrentInFlight;
                    if (inFlight != null)
                    {
                        if (inFlight.drivers != null)
                        {
                            driverCount = inFlight.drivers.Count;
                            for (int i = 0; i < inFlight.drivers.Count; i++)
                            {
                                var d = inFlight.drivers[i];
                                if (d != null && d.samples != null) sampleSum += d.samples.Count;
                            }
                        }
                        if (inFlight.events != null) eventCount = inFlight.events.Count;
                    }
                }
                catch { }
                sb.Append(",\"inflight_drivers\":"); sb.Append(driverCount);
                sb.Append(",\"inflight_samples\":"); sb.Append(sampleSum);
                sb.Append(",\"inflight_events\":");  sb.Append(eventCount);

                sb.Append('}');
                _writer.WriteLine(sb.ToString());
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[MemoryProbe] write failed: {e.Message}");
                _disabled = true;
            }
        }

        private static string ResolveProjectRoot()
        {
            try
            {
                string dataPath = Application.dataPath;
                var dir = new System.IO.DirectoryInfo(dataPath);
                while (dir != null)
                {
                    if (System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "Assets")) &&
                        System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "Packages")))
                        return dir.FullName;
                    dir = dir.Parent;
                }
            }
            catch { }
            return null;
        }
    }

}
