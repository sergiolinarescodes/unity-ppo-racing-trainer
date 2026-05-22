using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.AiDriver.Config;
using UnityPpoRacingTrainer.Core.AiDriver.Physics;
using UnityPpoRacingTrainer.Core.AiDriver.Physics.Draft;
using UnityPpoRacingTrainer.Core.AiDriver.Physics.Fuel;
using UnityPpoRacingTrainer.Core.AiDriver.Physics.Tires;
using UnityPpoRacingTrainer.Core.AiDriver.Policy;
using UnityPpoRacingTrainer.Core.AiDriver.Race;
using UnityPpoRacingTrainer.Core.AiDriver.Training.Stages;
using UnityPpoRacingTrainer.Core.Track.Loop;
using Unidad.Core.EventBus;
using Unidad.Core.Systems;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.AiDriver.Training
{
    /// <summary>
    /// Pluggable per-car reward shaper for personality-aware training.
    /// Subscribes to the physics events surfaced by the optional side-systems
    /// (tire puncture, draft, car-car contact, overtake, fuel depletion) and
    /// accumulates a personality-weighted reward delta plus an optional
    /// termination reason. <see cref="EpisodeRunner"/> drains the shaper at
    /// the end of <c>PostStep</c> and folds the result into its
    /// <see cref="StepResult"/>. When the side-systems are not registered,
    /// the shaper contributes zero, so the base reward path stays unchanged.
    /// Tuning coefficients (per-source magnitudes, thresholds, time windows)
    /// load from <see cref="ITrainingSettingsService"/> — settings.json →
    /// rewardShaper.* — edit and restart to retune. A small set of
    /// *structural* shape constants (personality jitter width, tire-overstress
    /// curve threshold, draft personality floor, etc.) are intentionally baked
    /// in code rather than exposed; see the "Baked constants" region in the
    /// implementation.
    /// </summary>
    public interface IRewardShaper
    {
        void OnEpisodeBegin(CarId carId);
        StepResult Drain(CarId carId);
        StepResult AccumulatePerTick(CarId carId, float dt);
    }

    internal sealed class RewardShaper : SystemServiceBase, IRewardShaper
    {
        // Overtake shaping (rewardShaper.overtakes.*)
        private readonly float _overtakeBonus;
        private readonly float _overtakeHoldPerSectorBase;
        private readonly int _overtakeHoldSectorCap;
        private readonly float _overtakeFullLapHeldBonus;
        private readonly float _gotPassedPenalty;
        private readonly float _gridGraceSeconds;
        private readonly float _minPassingAggression;
        private readonly float _firstSectorAggressionMin;
        private readonly float _firstSectorAggressionMax;

        // Position / clean-driving shaping (rewardShaper.position.*)
        private readonly float _passivePositionPerSectorScale;
        private readonly float _passivePositionPerLapScale;
        private readonly float _cleanDrivingBonusPerSec;
        private readonly float _cleanDrivingWindowSec;
        private readonly float _cleanRaceBonusTerminal;
        private readonly float _holdPositionBonusPerSec;
        private readonly float _sectorCleanBonus;
        private readonly float _sectorCleanHealthThreshold;

        // Contact / rear-end shaping (rewardShaper.contact.*)
        private readonly float _carCrashCoef;
        private readonly float _rearEndOffenderMul;
        private readonly float _rearEndOffenderFlatPenalty;
        private readonly float _lowHpVictimThreshold;
        private readonly float _lowHpVictimExtraPenalty;
        private readonly float _destroyVictimExtraPenalty;
        private readonly float _destroyedHealthEpsilon;
        private readonly float _rearEndVictimMul;
        private readonly float _rearEndCooldownSec;
        private readonly float _overtakeGraceSec;
        private readonly float _minContactImpactForFlatPenaltyMS;

        // Threat / avoidance shaping (rewardShaper.threat.*)
        private readonly float _threatRayMaxMeters;
        private readonly float _threatClosingMinMS;
        private readonly float _threatClearWindowSec;
        private readonly float _threatTireWaiverScale;
        private readonly float _threatPatienceWindowSec;
        private readonly float _avoidanceCoeff;
        private readonly float _avoidanceClosingCapMS;
        private readonly float _minAvoidanceClearSpeedMS;
        private readonly float _stuckInThreatPenaltyPerSec;
        private readonly float _stuckSpeedThresholdMS;

        // Pack racing (rewardShaper.pack.*)
        private readonly float _packProximityBonusPerSec;
        private readonly float _packProximityRadiusM;
        private readonly int _packProximityCarCountCap;
        private readonly float _packProximityMinSpeedFrac;
        private readonly float _cleanFollowBonusPerSec;
        private readonly float _cleanFollowMaxDistM;
        private readonly float _cleanFollowMinDistM;
        private readonly float _cleanFollowMinHoldSec;
        private readonly float _cleanFollowMaxHoldSec;
        private readonly int _packRacingMinDriverCount;

        // Pace / historical-best (rewardShaper.pace.*)
        private readonly float _beatBestLapBonus;
        private readonly float _matchBestLapBonus;
        private readonly float _matchBestLapToleranceFrac;
        private readonly float _paceShapingPerStep;
        private readonly float _paceShapingMaxPerLap;
        private readonly float _paceProjectionMinArcFrac;
        private readonly float _paceProjectionMargin;
        private readonly float _peakPaceMinMultiplier;
        private readonly float _peakPaceMaxMultiplier;
        private readonly float _peakPaceSectorImproveCoeff;
        private readonly float _peakPaceSectorEmaAlpha;
        private readonly float _peakPaceSectorMaxAbsDeltaSec;

        // Draft + consumables (rewardShaper.draftAndConsumables.*)
        private readonly float _draftBonusPerSec;
        private readonly float _draftPassBonus;
        private readonly float _draftPassMinStrength;
        private readonly float _draftPassLookbackSec;
        private readonly float _tireOverstressPenaltyPerSec;
        private readonly float _fuelMarginPenaltyPerSec;
        private readonly float _fuelOutPenalty;
        private readonly float _punctureOffTrackPenalty;

        // ----------------------------------------------------------------------
        // Baked constants — intentionally NOT in settings.json.
        // These shape the *structure* of the reward function rather than its
        // tuning surface. Exposing them would let a contributor change reward
        // semantics in ways the trained policy was never exposed to; better to
        // require a code edit (and a versioned snapshot) for changes.
        // ----------------------------------------------------------------------

        // Width of the uniform jitter applied to each personality archetype scalar
        // when sampling a driver. Keeps the policy from overfitting to discrete
        // archetypes while preserving the archetype's centre of mass.
        private const float PersonalityJitterHalfWidth = 0.08f;

        // Abundant-fuel sentinel: matches the value the trainer's "ignore fuel"
        // stage feature treats as effectively-infinite without rewriting the
        // fuel pipeline.
        private const float AbundantStartingLiters = 100f;

        // Constrained-fuel sampling: cluster around (lapsTarget * litersPerLap).
        // The 25 here matches the canonical "1 lap of fuel = 25L" baseline used
        // by FuelService; the [0.6, 1.4] band gives the policy laps where it
        // must lift-coast and laps where it has comfortable margin.
        private const float ConstrainedFuelLapMultiplierMin = 0.6f;
        private const float ConstrainedFuelLapMultiplierMax = 1.4f;
        private const float ConstrainedFuelLitersPerLapBase = 25f;
        private const float ConstrainedFuelMinLiters = 2f;

        // Tire overstress kicks in past 85% wear. Both the threshold and the
        // 15% window above it are physics-correlated (puncture starts to spike
        // at 90% wear in TirePhysicsService); exposing them risks decoupling
        // shaping from the physics signal the policy actually observes.
        private const float TireOverstressWearThreshold = 0.85f;
        private const float TireOverstressWearWindow = 0.15f;

        // Tire-penalty personality multiplier. Range [0.5, 1.5] across the
        // TirePreservation [0, 1] axis: a tire-careless driver still gets half
        // the penalty (otherwise the gradient vanishes), a careful driver gets
        // 1.5x. The 0.5 floor is what makes this *not* settings-exposed.
        private const float TirePenaltyPersonalityFloor = 0.5f;

        // Aggression-patience scaling for the tire waiver: half the
        // patience window is "always available", the other half scales by
        // (1 - aggression). Same 50/50 logic underpins the threat waiver.
        private const float ThreatPatienceAggressionFloor = 0.5f;

        // Draft bonus is split 50/50 between "everyone gets some draft credit"
        // (0.5 floor) and "aggressive drivers get more" (up to +0.4 at
        // PassingAggression = 1). Hard-coded so the draft signal stays
        // recognisable across personalities.
        private const float DraftBonusBaseFraction = 0.5f;
        private const float DraftBonusAggressionGain = 0.4f;

        // Decay constant used when EMA-tracking RecentDraftPeak. Mathf.Exp
        // hits ~0.37 at one half-life, so we want the half-life to be half of
        // the configured _draftPassLookbackSec — hence the 0.5 multiplier.
        private const float DraftPeakHalfLifeFraction = 0.5f;

        // Fuel-margin penalty personality multiplier: (0.5 + FuelEconomy ∈ [0,1])
        // → range [0.5, 1.5]. Same 0.5 floor pattern as tire — guarantees a
        // non-zero gradient for fuel-careless drivers.
        private const float FuelMarginPersonalityFloor = 0.5f;

        // "Clean driving" envelope: scales hold-position credit by an
        // aggression-inverse term. floor=0.5 ensures even max-aggression drivers
        // get baseline clean-driving signal; gain=0.7 lets cautious drivers
        // earn ~70% more on top of the floor.
        private const float CleanScaleFloor = 0.5f;
        private const float CleanScaleAggressionGain = 0.7f;

        // Overtake / draft-pass bonus shaping: (0.5 + 0.8 * PassingAggression)
        // → range [0.5, 1.3]. Aggressive drivers earn more for the pass, but
        // there's a 0.5 floor so passive drivers still feel the gradient.
        private const float OvertakeBonusAggressionFloor = 0.5f;
        private const float OvertakeBonusAggressionGain = 0.8f;

        // Position-hold ("cementing the pass") bonus shaping. Slightly steeper
        // than overtake bonus: 0.55 floor + 0.88 gain → range [0.55, 1.43].
        // Same family as overtake; intentionally not collapsed onto the same
        // const so the asymmetry between "earn the pass" and "hold the pass"
        // stays adjustable as separate semantic knobs.
        private const float HoldPositionAggressionFloor = 0.55f;
        private const float HoldPositionAggressionGain = 0.88f;

        // Run-id stamped onto records.json writes. Best-effort: pulled from
        // an env var the supervisor sets; falls back to "trainer" when
        // running ad-hoc.
        private static readonly string s_runIdForRecords =
            Environment.GetEnvironmentVariable("PPO_RACING_RUN_ID") ?? "trainer";

        private float PeakPaceMultiplier(float peakPaceBias01)
        {
            return Mathf.Lerp(_peakPaceMinMultiplier, _peakPaceMaxMultiplier,
                Mathf.Clamp01(peakPaceBias01));
        }

        // Latest historical best for the active circuit. 0 when unknown.
        private float _currentCircuitBestLapSec;
        private string _currentCircuitId = string.Empty;

        private readonly IActiveStageProfile _active;
        private readonly ITirePhysicsService _tires;
        private readonly IFuelService _fuel;
        private readonly IDraftService _draft;
        private readonly IRaceStateService _race;
        private readonly IClosedLoopService _loop;
        private readonly ICarSimulationService _sim;
        private readonly IDriverPhysicsRegistry _registry;

        private readonly Dictionary<CarId, AccumState> _accum = new();
        private readonly Dictionary<CarId, List<OvertakePending>> _pending = new();

        public RewardShaper(
            IEventBus eventBus,
            ITrainingSettingsService settings,
            IActiveStageProfile active,
            ITirePhysicsService tires,
            IFuelService fuel,
            IDraftService draft,
            IRaceStateService race,
            IClosedLoopService loop,
            IDriverPhysicsRegistry registry = null,
            ICarSimulationService sim = null) : base(eventBus)
        {
            var r = settings.Current.RewardShaper;

            _overtakeBonus = r.Overtakes.OvertakeBonus;
            _overtakeHoldPerSectorBase = r.Overtakes.OvertakeHoldPerSectorBase;
            _overtakeHoldSectorCap = r.Overtakes.OvertakeHoldSectorCap;
            _overtakeFullLapHeldBonus = r.Overtakes.OvertakeFullLapHeldBonus;
            _gotPassedPenalty = r.Overtakes.GotPassedPenalty;
            _gridGraceSeconds = r.Overtakes.GridGraceSeconds;
            _minPassingAggression = r.Overtakes.MinPassingAggression;
            _firstSectorAggressionMin = r.Overtakes.FirstSectorAggressionMin;
            _firstSectorAggressionMax = r.Overtakes.FirstSectorAggressionMax;

            _passivePositionPerSectorScale = r.Position.PassivePositionPerSectorScale;
            _passivePositionPerLapScale = r.Position.PassivePositionPerLapScale;
            _cleanDrivingBonusPerSec = r.Position.CleanDrivingBonusPerSec;
            _cleanDrivingWindowSec = r.Position.CleanDrivingWindowSec;
            _cleanRaceBonusTerminal = r.Position.CleanRaceBonusTerminal;
            _holdPositionBonusPerSec = r.Position.HoldPositionBonusPerSec;
            _sectorCleanBonus = r.Position.SectorCleanBonus;
            _sectorCleanHealthThreshold = r.Position.SectorCleanHealthThreshold;

            _carCrashCoef = r.Contact.CarCrashCoef;
            _rearEndOffenderMul = r.Contact.RearEndOffenderMul;
            _rearEndOffenderFlatPenalty = r.Contact.RearEndOffenderFlatPenalty;
            _lowHpVictimThreshold = r.Contact.LowHpVictimThreshold;
            _lowHpVictimExtraPenalty = r.Contact.LowHpVictimExtraPenalty;
            _destroyVictimExtraPenalty = r.Contact.DestroyVictimExtraPenalty;
            _destroyedHealthEpsilon = r.Contact.DestroyedHealthEpsilon;
            _rearEndVictimMul = r.Contact.RearEndVictimMul;
            _rearEndCooldownSec = r.Contact.RearEndCooldownSec;
            _overtakeGraceSec = r.Contact.OvertakeGraceSec;
            _minContactImpactForFlatPenaltyMS = r.Contact.MinContactImpactForFlatPenaltyMS;

            _threatRayMaxMeters = r.Threat.RayMaxMeters;
            _threatClosingMinMS = r.Threat.ClosingMinMS;
            _threatClearWindowSec = r.Threat.ClearWindowSec;
            _threatTireWaiverScale = r.Threat.TireWaiverScale;
            _threatPatienceWindowSec = r.Threat.PatienceWindowSec;
            _avoidanceCoeff = r.Threat.AvoidanceCoeff;
            _avoidanceClosingCapMS = r.Threat.AvoidanceClosingCapMS;
            _minAvoidanceClearSpeedMS = r.Threat.MinAvoidanceClearSpeedMS;
            _stuckInThreatPenaltyPerSec = r.Threat.StuckInThreatPenaltyPerSec;
            _stuckSpeedThresholdMS = r.Threat.StuckSpeedThresholdMS;

            _packProximityBonusPerSec = r.Pack.PackProximityBonusPerSec;
            _packProximityRadiusM = r.Pack.PackProximityRadiusM;
            _packProximityCarCountCap = r.Pack.PackProximityCarCountCap;
            _packProximityMinSpeedFrac = r.Pack.PackProximityMinSpeedFrac;
            _cleanFollowBonusPerSec = r.Pack.CleanFollowBonusPerSec;
            _cleanFollowMaxDistM = r.Pack.CleanFollowMaxDistM;
            _cleanFollowMinDistM = r.Pack.CleanFollowMinDistM;
            _cleanFollowMinHoldSec = r.Pack.CleanFollowMinHoldSec;
            _cleanFollowMaxHoldSec = r.Pack.CleanFollowMaxHoldSec;
            _packRacingMinDriverCount = r.Pack.PackRacingMinDriverCount;

            _beatBestLapBonus = r.Pace.BeatBestLapBonus;
            _matchBestLapBonus = r.Pace.MatchBestLapBonus;
            _matchBestLapToleranceFrac = r.Pace.MatchBestLapToleranceFrac;
            _paceShapingPerStep = r.Pace.PaceShapingPerStep;
            _paceShapingMaxPerLap = r.Pace.PaceShapingMaxPerLap;
            _paceProjectionMinArcFrac = r.Pace.PaceProjectionMinArcFrac;
            _paceProjectionMargin = r.Pace.PaceProjectionMargin;
            _peakPaceMinMultiplier = r.Pace.PeakPaceMinMultiplier;
            _peakPaceMaxMultiplier = r.Pace.PeakPaceMaxMultiplier;
            _peakPaceSectorImproveCoeff = r.Pace.PeakPaceSectorImproveCoeff;
            _peakPaceSectorEmaAlpha = r.Pace.PeakPaceSectorEmaAlpha;
            _peakPaceSectorMaxAbsDeltaSec = r.Pace.PeakPaceSectorMaxAbsDeltaSec;

            _draftBonusPerSec = r.DraftAndConsumables.DraftBonusPerSec;
            _draftPassBonus = r.DraftAndConsumables.DraftPassBonus;
            _draftPassMinStrength = r.DraftAndConsumables.DraftPassMinStrength;
            _draftPassLookbackSec = r.DraftAndConsumables.DraftPassLookbackSec;
            _tireOverstressPenaltyPerSec = r.DraftAndConsumables.TireOverstressPenaltyPerSec;
            _fuelMarginPenaltyPerSec = r.DraftAndConsumables.FuelMarginPenaltyPerSec;
            _fuelOutPenalty = r.DraftAndConsumables.FuelOutPenalty;
            _punctureOffTrackPenalty = r.DraftAndConsumables.PunctureOffTrackPenalty;

            _active = active;
            _tires = tires;
            _fuel = fuel;
            _draft = draft;
            _race = race;
            _loop = loop;
            _registry = registry;
            _sim = sim;

            Subscribe<OvertakeEvent>(OnOvertake);
            Subscribe<CarHitCarEvent>(OnCarHit);
            Subscribe<FuelDepletedEvent>(OnFuelOut);
            Subscribe<TirePuncturedEvent>(OnPuncture);
            Subscribe<CarOffTrackEvent>(OnOffTrack);
            Subscribe<MicroSectorPassedEvent>(OnMicroSector);
            Subscribe<CarLapCompletedEvent>(OnLapCompleted);
            Subscribe<CircuitBestLapKnownEvent>(OnCircuitBestLapKnown);
        }

        private void OnCircuitBestLapKnown(CircuitBestLapKnownEvent e)
        {
            _currentCircuitBestLapSec = e.BestLapSeconds;
            _currentCircuitId = e.CircuitId ?? string.Empty;
        }

        private DriverPersonality PersonalityOf(CarId id)
            => _accum.TryGetValue(id, out var s) ? s.Personality : DriverPersonality.Default;

        private float EffectivePassingAggression(CarId id)
        {
            if (!_accum.TryGetValue(id, out var s)) return DriverPersonality.Default.PassingAggression;
            float baseAgg = s.Personality.PassingAggression;
            if (!s.InFirstSector) return baseAgg;
            return Mathf.Lerp(_firstSectorAggressionMin, _firstSectorAggressionMax, baseAgg);
        }

        public void OnEpisodeBegin(CarId carId)
        {
            var personality = SamplePersonalityForCurrentEpisode();
            _accum[carId] = new AccumState
            {
                Personality = personality,
                SectorCleanCurrent = true,
                InFirstSector = true,
                LapStartElapsedSec = 0f,
                LastMicroIndex = 0,
                PaceShapingAccumThisLap = 0f,
                CachedMicroCount = 0,
                LapCount = 0,
            };
            if (_pending.TryGetValue(carId, out var list)) list.Clear();
            foreach (var kv in _pending)
            {
                var l = kv.Value;
                for (int i = l.Count - 1; i >= 0; i--)
                    if (l[i].Passed.Value == carId.Value) l.RemoveAt(i);
            }

            float startingLiters = SampleStartingLiters();
            _registry?.Register(carId, personality, startingLiters);
        }

        internal DriverPersonality SamplePersonalityForCurrentEpisode()
        {
            if (_active.Current.Personality == PersonalitySamplingMode.Uniform)
            {
                return new DriverPersonality(
                    TirePreservation: UnityEngine.Random.value,
                    FuelEconomy: UnityEngine.Random.value,
                    PassingAggression: Mathf.Lerp(_minPassingAggression, 1f, UnityEngine.Random.value),
                    DefendingResolve: UnityEngine.Random.value,
                    RiskTolerance: UnityEngine.Random.value,
                    PeakPaceBias: UnityEngine.Random.value,
                    Reserved0: 0f,
                    Reserved1: 0f);
            }

            DriverPersonality archetype = (UnityEngine.Random.Range(0, 6)) switch
            {
                0 => DriverPersonality.TirePreserver,
                1 => DriverPersonality.FuelSaver,
                2 => DriverPersonality.Attacker,
                3 => DriverPersonality.Defender,
                4 => DriverPersonality.AllRounder,
                _ => DriverPersonality.RiskTaker,
            };
            const float j = PersonalityJitterHalfWidth;
            return new DriverPersonality(
                TirePreservation: Mathf.Clamp01(archetype.TirePreservation + UnityEngine.Random.Range(-j, j)),
                FuelEconomy: Mathf.Clamp01(archetype.FuelEconomy + UnityEngine.Random.Range(-j, j)),
                PassingAggression: Mathf.Clamp(archetype.PassingAggression + UnityEngine.Random.Range(-j, j), _minPassingAggression, 1f),
                DefendingResolve: Mathf.Clamp01(archetype.DefendingResolve + UnityEngine.Random.Range(-j, j)),
                RiskTolerance: Mathf.Clamp01(archetype.RiskTolerance + UnityEngine.Random.Range(-j, j)),
                PeakPaceBias: Mathf.Clamp01(archetype.PeakPaceBias + UnityEngine.Random.Range(-j, j)),
                Reserved0: 0f,
                Reserved1: 0f);
        }

        internal float SampleStartingLiters()
        {
            if (_active.Current.Fuel == FuelSamplingMode.Abundant) return AbundantStartingLiters;
            float lapsMargin = UnityEngine.Random.Range(
                ConstrainedFuelLapMultiplierMin, ConstrainedFuelLapMultiplierMax);
            return Mathf.Max(ConstrainedFuelMinLiters,
                lapsMargin * ConstrainedFuelLitersPerLapBase);
        }

        public StepResult Drain(CarId carId)
        {
            if (!_accum.TryGetValue(carId, out var s)) return StepResult.None;
            var r = new StepResult(s.PendingReward, s.PendingEnd);
            s.PendingReward = 0f;
            s.PendingEnd = null;
            _accum[carId] = s;
            return r;
        }

        public StepResult AccumulatePerTick(CarId carId, float dt)
        {
            if (!_accum.TryGetValue(carId, out var s)) s = new AccumState();

            var profile = PersonalityOf(carId);
            float reward = 0f;

            bool draftOn = _active == null || _active.Has(StageFeature.DraftBonus);
            bool tireOn  = _active == null || _active.Has(StageFeature.TireOverstressPenalty);
            bool fuelOn  = _active == null || _active.Has(StageFeature.FuelMarginPenalty);
            bool holdOn  = _active == null || _active.Has(StageFeature.HoldPositionBonus);
            bool cleanOn = _active == null || _active.Has(StageFeature.CleanDrivingBonus);

            if (draftOn && _draft != null)
            {
                var d = _draft.Get(carId);
                reward += _draftBonusPerSec * d.Strength
                          * (DraftBonusBaseFraction
                             + DraftBonusAggressionGain * profile.PassingAggression) * dt;
                float decay = Mathf.Exp(-dt / Mathf.Max(0.1f,
                    _draftPassLookbackSec * DraftPeakHalfLifeFraction));
                s.RecentDraftPeak = Mathf.Max(d.Strength, s.RecentDraftPeak * decay);
            }
            else
            {
                float decay = Mathf.Exp(-dt / Mathf.Max(0.1f,
                    _draftPassLookbackSec * DraftPeakHalfLifeFraction));
                s.RecentDraftPeak *= decay;
            }

            bool packBonusesOn = _sim != null && _sim.ActiveCars.Count >= _packRacingMinDriverCount;
            if (!packBonusesOn) s.CleanFollowHoldSec = 0f;
            if (packBonusesOn && _sim.TryGetState(carId, out var selfState))
            {
                int nearby = 0;
                float nearestDist2 = float.MaxValue;
                Vector3 sp = selfState.Position;
                float radius2 = _packProximityRadiusM * _packProximityRadiusM;
                float followRadius2 = _cleanFollowMaxDistM * _cleanFollowMaxDistM;
                foreach (var id in _sim.ActiveCars)
                {
                    if (id.Value == carId.Value) continue;
                    if (!_sim.TryGetState(id, out var os)) continue;
                    float dx = os.Position.x - sp.x;
                    float dz = os.Position.z - sp.z;
                    float d2 = dx * dx + dz * dz;
                    if (d2 <= radius2) nearby++;
                    if (d2 < nearestDist2) nearestDist2 = d2;
                }

                float maxSpeed = Mathf.Max(0.01f, AiDriverPhysicsDefaults.Latest.MaxSpeed);
                float speedFrac = Mathf.Clamp01(selfState.VelocityXZ.magnitude / maxSpeed);

                if (nearby > 0 && speedFrac >= _packProximityMinSpeedFrac && s.CleanCooldownSec <= 0f)
                {
                    int credited = Mathf.Min(nearby, _packProximityCarCountCap);
                    reward += _packProximityBonusPerSec * credited * speedFrac * dt;
                }

                float followFloor2 = _cleanFollowMinDistM * _cleanFollowMinDistM;
                if (nearestDist2 >= followFloor2 && nearestDist2 <= followRadius2 && s.CleanCooldownSec <= 0f)
                {
                    s.CleanFollowHoldSec += dt;
                    if (s.CleanFollowHoldSec >= _cleanFollowMinHoldSec)
                    {
                        float held = Mathf.Min(s.CleanFollowHoldSec, _cleanFollowMaxHoldSec);
                        float scale = held / Mathf.Max(0.01f, _cleanFollowMinHoldSec);
                        reward += _cleanFollowBonusPerSec * scale * dt;
                    }
                }
                else
                {
                    s.CleanFollowHoldSec = 0f;
                }
            }

            bool inThreat = UpdateThreatState(carId, ref s, dt);

            if (tireOn && _tires != null)
            {
                var t = _tires.Get(carId);
                if (t.Worst > TireOverstressWearThreshold)
                {
                    float over = (t.Worst - TireOverstressWearThreshold) / TireOverstressWearWindow;
                    float weight = _tireOverstressPenaltyPerSec
                                   * (TirePenaltyPersonalityFloor + profile.TirePreservation);
                    float effAgg = EffectivePassingAggression(carId);
                    float aggressionPatience = Mathf.Clamp01(1f - effAgg);
                    float patienceWindow = _threatPatienceWindowSec
                                           * (ThreatPatienceAggressionFloor + aggressionPatience);
                    bool waiverActive = inThreat && s.ThreatActiveSec < patienceWindow;
                    float baseScale = waiverActive ? _threatTireWaiverScale : 1f;
                    float scale = Mathf.Lerp(baseScale, 1f, effAgg);
                    reward -= weight * over * over * scale * dt;
                }
            }

            if (fuelOn && _fuel != null)
            {
                var f = _fuel.Get(carId);
                if (f.RollingLapsRemaining < 1f && !f.Depleted)
                {
                    float shortfall = 1f - f.RollingLapsRemaining;
                    reward -= _fuelMarginPenaltyPerSec * shortfall
                              * (FuelMarginPersonalityFloor + profile.FuelEconomy) * dt;
                }
            }

            if (holdOn)
                reward += _holdPositionBonusPerSec * profile.DefendingResolve * dt;

            s.CleanCooldownSec = Mathf.Max(0f, s.CleanCooldownSec - dt);
            s.RearEndCooldownSec = Mathf.Max(0f, s.RearEndCooldownSec - dt);
            s.OvertakeGraceSec = Mathf.Max(0f, s.OvertakeGraceSec - dt);
            if (cleanOn && s.CleanCooldownSec <= 0f)
            {
                float cleanScale = CleanScaleFloor
                                   + CleanScaleAggressionGain * (1f - profile.PassingAggression);
                reward += _cleanDrivingBonusPerSec * cleanScale * dt;
            }

            if (_currentCircuitBestLapSec > 0f
                && s.CachedMicroCount > 0
                && s.LastMicroIndex > 0
                && s.PaceShapingAccumThisLap < _paceShapingMaxPerLap)
            {
                float lapElapsed = s.EpisodeElapsed - s.LapStartElapsedSec;
                if (lapElapsed > 0.1f)
                {
                    float arcFrac = (s.LastMicroIndex + 0.5f) / Mathf.Max(1, s.CachedMicroCount);
                    if (arcFrac >= _paceProjectionMinArcFrac)
                    {
                        float projected = lapElapsed / arcFrac;
                        float gapFrac = (projected - _currentCircuitBestLapSec) / Mathf.Max(0.1f, _currentCircuitBestLapSec);
                        float wideMargin = 3f * _paceProjectionMargin;
                        float score = 1f - Mathf.Clamp01(Mathf.Max(0f, gapFrac) / wideMargin);
                        if (score > 0f)
                        {
                            float peakMul = PeakPaceMultiplier(profile.PeakPaceBias);
                            float paceBonus = score * _paceShapingPerStep * peakMul * dt;
                            float room = _paceShapingMaxPerLap - s.PaceShapingAccumThisLap;
                            if (paceBonus > room) paceBonus = room;
                            if (paceBonus > 0f)
                            {
                                reward += paceBonus;
                                s.PaceShapingAccumThisLap += paceBonus;
                            }
                        }
                    }
                }
            }

            s.EpisodeElapsed += dt;
            s.PendingReward += reward;
            _accum[carId] = s;
            return new StepResult(reward, null);
        }

        private void OnOvertake(OvertakeEvent e)
        {
            if (_active != null &&
                !_active.Has(StageFeature.OvertakeReward) &&
                !_active.Has(StageFeature.GotPassedPenalty))
                return;

            bool passerGraced = _accum.TryGetValue(e.Passer, out var sPCheck)
                                && sPCheck.EpisodeElapsed < _gridGraceSeconds;
            bool passedGraced = _accum.TryGetValue(e.Passed, out var sVCheck)
                                && sVCheck.EpisodeElapsed < _gridGraceSeconds;
            if (passerGraced || passedGraced) return;

            if (_accum.TryGetValue(e.Passer, out var sP))
            {
                var passerProf = sP.Personality;
                sP.PendingReward += _overtakeBonus
                    * (OvertakeBonusAggressionFloor
                       + OvertakeBonusAggressionGain * passerProf.PassingAggression);
                if (sP.RecentDraftPeak >= _draftPassMinStrength)
                {
                    sP.PendingReward += _draftPassBonus
                        * (OvertakeBonusAggressionFloor
                           + OvertakeBonusAggressionGain * passerProf.PassingAggression);
                }
                sP.OvertakeGraceSec = _overtakeGraceSec;
                _accum[e.Passer] = sP;
            }

            if (_accum.TryGetValue(e.Passed, out var sV))
            {
                sV.PendingReward -= _gotPassedPenalty;
                sV.OvertakeGraceSec = _overtakeGraceSec;
                _accum[e.Passed] = sV;
            }

            if (_pending.TryGetValue(e.Passed, out var passedList))
            {
                for (int i = passedList.Count - 1; i >= 0; i--)
                    if (passedList[i].Passed.Value == e.Passer.Value)
                        passedList.RemoveAt(i);
            }

            if (!_pending.TryGetValue(e.Passer, out var passerList))
            {
                passerList = new List<OvertakePending>(4);
                _pending[e.Passer] = passerList;
            }
            bool exists = false;
            for (int i = 0; i < passerList.Count; i++)
            {
                if (passerList[i].Passed.Value == e.Passed.Value) { exists = true; break; }
            }
            if (!exists)
                passerList.Add(new OvertakePending { Passed = e.Passed, SectorsHeld = 0 });
        }

        private void OnMicroSector(MicroSectorPassedEvent e)
        {
            if (_accum.TryGetValue(e.CarId, out var sClean))
            {
                if (sClean.SectorCleanCurrent && !sClean.SectorCleanAwarded)
                {
                    float health = 1f;
                    if (_sim != null) _sim.TryGetHealth(e.CarId, out health);
                    if (health > _sectorCleanHealthThreshold)
                    {
                        sClean.PendingReward += _sectorCleanBonus;
                        sClean.SectorCleanAwarded = true;
                    }
                }
                sClean.SectorCleanCurrent = true;
                sClean.InFirstSector = false;

                int peakPaceMicroCount = ResolveMicroCount();
                if (peakPaceMicroCount > 0)
                {
                    if (sClean.BestSectorTime == null || sClean.BestSectorTime.Length != peakPaceMicroCount)
                        sClean.BestSectorTime = new float[peakPaceMicroCount];
                    int idx = e.Micro;
                    if ((uint)idx < (uint)peakPaceMicroCount)
                    {
                        float elapsed = sClean.EpisodeElapsed - sClean.LastSectorCrossAtSec;
                        float expected = sClean.BestSectorTime[idx];
                        if (expected > 0f && elapsed > 0f)
                        {
                            float delta = expected - elapsed;
                            if (delta >  _peakPaceSectorMaxAbsDeltaSec) delta =  _peakPaceSectorMaxAbsDeltaSec;
                            if (delta < -_peakPaceSectorMaxAbsDeltaSec) delta = -_peakPaceSectorMaxAbsDeltaSec;
                            sClean.PendingReward += _peakPaceSectorImproveCoeff
                                                  * sClean.Personality.PeakPaceBias
                                                  * delta;
                        }
                        if (expected <= 0f)
                            sClean.BestSectorTime[idx] = elapsed > 0f ? elapsed : 0f;
                        else
                            sClean.BestSectorTime[idx] = (1f - _peakPaceSectorEmaAlpha) * expected
                                                       + _peakPaceSectorEmaAlpha * elapsed;
                        sClean.LastSectorCrossAtSec = sClean.EpisodeElapsed;
                    }
                    sClean.LastMicroIndex = idx;
                    sClean.CachedMicroCount = peakPaceMicroCount;
                }

                _accum[e.CarId] = sClean;
            }

            bool posBonusOn = _active == null || _active.Has(StageFeature.MicroSectorPositionBonus);
            bool holdOn     = _active == null || _active.Has(StageFeature.OvertakeReward);
            if (!posBonusOn && !holdOn) return;

            if (posBonusOn)
            {
                float posWeight = PositionWeight(_race?.GetPosition(e.CarId) ?? 0);
                if (posWeight > 0f && _accum.TryGetValue(e.CarId, out var sCar))
                {
                    sCar.PendingReward += _passivePositionPerSectorScale * posWeight;
                    _accum[e.CarId] = sCar;
                }
            }

            if (!holdOn) return;

            if (!_pending.TryGetValue(e.CarId, out var list) || list.Count == 0) return;

            int microCount = ResolveMicroCount();
            int holdCap = Mathf.Min(_overtakeHoldSectorCap, Mathf.Max(microCount, 1));

            for (int i = list.Count - 1; i >= 0; i--)
            {
                var pending = list[i];
                int passerPos = _race?.GetPosition(e.CarId) ?? 0;
                int passedPos = _race?.GetPosition(pending.Passed) ?? 0;
                bool stillAhead = passerPos > 0 && passedPos > 0 && passerPos < passedPos;

                if (!stillAhead)
                {
                    list.RemoveAt(i);
                    continue;
                }

                pending.SectorsHeld++;
                int payoutSectors = Mathf.Min(pending.SectorsHeld, holdCap);
                if (_accum.TryGetValue(e.CarId, out var sP))
                {
                    var prof = sP.Personality;
                    float scale = HoldPositionAggressionFloor
                                  + HoldPositionAggressionGain * prof.PassingAggression;
                    sP.PendingReward += _overtakeHoldPerSectorBase * payoutSectors * scale;
                    _accum[e.CarId] = sP;
                }

                if (microCount > 0 && pending.SectorsHeld >= microCount)
                {
                    if (_accum.TryGetValue(e.CarId, out var sCement))
                    {
                        var prof = sCement.Personality;
                        sCement.PendingReward += _overtakeFullLapHeldBonus
                            * (HoldPositionAggressionFloor
                               + HoldPositionAggressionGain * prof.PassingAggression);
                        _accum[e.CarId] = sCement;
                    }
                    list.RemoveAt(i);
                    continue;
                }

                list[i] = pending;
            }
        }

        private void OnLapCompleted(CarLapCompletedEvent e)
        {
            if (_accum.TryGetValue(e.Id, out var sBest))
            {
                sBest.LapCount++;
                bool isFlying = sBest.LapCount >= 2;
                float lapSec = e.LapTimeSeconds;
                float best = _currentCircuitBestLapSec;
                if (isFlying && lapSec > 0f)
                {
                    float peakMul = PeakPaceMultiplier(sBest.Personality.PeakPaceBias);
                    if (best > 0f)
                    {
                        if (lapSec < best)
                        {
                            sBest.PendingReward += _beatBestLapBonus * peakMul;
                        }
                        else if (lapSec <= best * (1f + _matchBestLapToleranceFrac))
                        {
                            sBest.PendingReward += _matchBestLapBonus * peakMul;
                        }
                    }
                    if (!string.IsNullOrEmpty(_currentCircuitId)
                        && (best <= 0f || lapSec < best))
                    {
                        if (CircuitRecordsStore.TryUpsertBestLap(_currentCircuitId, lapSec, s_runIdForRecords))
                        {
                            _currentCircuitBestLapSec = lapSec;
                            TrainingTelemetry.Emit(
                                $"{{\"event\":\"lap_vs_best\",\"ts\":\"{DateTime.UtcNow:o}\","
                                + $"\"circuit\":\"{_currentCircuitId}\","
                                + $"\"lap_s\":{lapSec.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)},"
                                + $"\"best_s\":{lapSec.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)},"
                                + $"\"delta_s\":{(best > 0f ? (lapSec - best).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "null")},"
                                + $"\"is_new_record\":true}}");
                        }
                    }
                    else if (best > 0f)
                    {
                        TrainingTelemetry.Emit(
                            $"{{\"event\":\"lap_vs_best\",\"ts\":\"{DateTime.UtcNow:o}\","
                            + $"\"circuit\":\"{_currentCircuitId}\","
                            + $"\"lap_s\":{lapSec.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)},"
                            + $"\"best_s\":{best.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)},"
                            + $"\"delta_s\":{(lapSec - best).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)},"
                            + $"\"is_new_record\":false}}");
                    }
                }
                sBest.LapStartElapsedSec = sBest.EpisodeElapsed;
                sBest.LastMicroIndex = 0;
                sBest.PaceShapingAccumThisLap = 0f;
                _accum[e.Id] = sBest;
            }

            if (_active != null && !_active.Has(StageFeature.LapPositionBonus)) return;

            float posWeight = PositionWeight(_race?.GetPosition(e.Id) ?? 0);
            if (posWeight > 0f && _accum.TryGetValue(e.Id, out var s))
            {
                s.PendingReward += _passivePositionPerLapScale * posWeight;
                _accum[e.Id] = s;
            }
        }

        private int ResolveMicroCount()
        {
            if (_loop != null && _loop.TryGetCurrentLoop(out var lp))
                return Mathf.Max(1, lp.Sectors.MicroCount);
            return 9;
        }

        /// <summary>
        /// Position → reward-multiplier table. Extended to P24 with a shallow
        /// gradient so every position carries a clear reward signal.
        /// </summary>
        private static float PositionWeight(int pos)
        {
            switch (pos)
            {
                case 1:  return 1.00f;
                case 2:  return 0.95f;
                case 3:  return 0.90f;
                case 4:  return 0.85f;
                case 5:  return 0.80f;
                case 6:  return 0.75f;
                case 7:  return 0.70f;
                case 8:  return 0.65f;
                case 9:  return 0.60f;
                case 10: return 0.55f;
                case 11: return 0.50f;
                case 12: return 0.45f;
                case 13: return 0.42f;
                case 14: return 0.39f;
                case 15: return 0.36f;
                case 16: return 0.33f;
                case 17: return 0.30f;
                case 18: return 0.28f;
                case 19: return 0.26f;
                case 20: return 0.24f;
                case 21: return 0.22f;
                case 22: return 0.20f;
                case 23: return 0.18f;
                case 24: return 0.16f;
                default: return pos > 24 ? 0.16f : 0f;
            }
        }

        private void OnCarHit(CarHitCarEvent e)
        {
            if (_active != null && !_active.Has(StageFeature.CarHitCarPenalty)) return;

            int posA = _race?.GetPosition(e.A) ?? 0;
            int posB = _race?.GetPosition(e.B) ?? 0;
            bool haveAccA = _accum.TryGetValue(e.A, out var stA);
            bool haveAccB = _accum.TryGetValue(e.B, out var stB);
            bool inGrace =
                (haveAccA && (stA.OvertakeGraceSec > 0f || stA.RearEndCooldownSec > 0f)) ||
                (haveAccB && (stB.OvertakeGraceSec > 0f || stB.RearEndCooldownSec > 0f));

            if (!inGrace && posA > 0 && posB > 0 && posA != posB)
            {
                CarId offender = posA > posB ? e.A : e.B;
                CarId victim   = posA > posB ? e.B : e.A;
                ApplyCrashPenalty(offender, e.ImpactSpeed, _rearEndOffenderMul);
                ApplyCrashPenalty(victim,   e.ImpactSpeed, _rearEndVictimMul);

                if (e.ImpactSpeed >= _minContactImpactForFlatPenaltyMS &&
                    _accum.TryGetValue(offender, out var sFlat))
                {
                    float extra = _rearEndOffenderFlatPenalty;
                    if (_sim != null && _sim.TryGetHealth(victim, out float vh))
                    {
                        if (vh <= _destroyedHealthEpsilon)
                            extra += _destroyVictimExtraPenalty + _lowHpVictimExtraPenalty;
                        else if (vh <= _lowHpVictimThreshold)
                            extra += _lowHpVictimExtraPenalty;
                    }
                    sFlat.PendingReward -= extra;
                    _accum[offender] = sFlat;
                }

                if (_accum.TryGetValue(offender, out var soff))
                {
                    soff.RearEndCooldownSec = _rearEndCooldownSec;
                    _accum[offender] = soff;
                }
                if (_accum.TryGetValue(victim, out var svic))
                {
                    svic.RearEndCooldownSec = _rearEndCooldownSec;
                    _accum[victim] = svic;
                }
            }
            else
            {
                ApplyCrashPenalty(e.A, e.ImpactSpeed, 1f);
                ApplyCrashPenalty(e.B, e.ImpactSpeed, 1f);
            }
        }

        private void ApplyCrashPenalty(CarId id, float impact, float multiplier)
        {
            if (!_accum.TryGetValue(id, out var s)) s = new AccumState();
            var prof = PersonalityOf(id);
            float weight = _carCrashCoef * Mathf.Clamp(1f - 0.4f * prof.PassingAggression, 0.4f, 1f);
            s.PendingReward -= weight * impact * impact * multiplier;
            if (impact >= _minContactImpactForFlatPenaltyMS)
            {
                s.CleanCooldownSec = _cleanDrivingWindowSec;
                s.SectorCleanCurrent = false;
                if (s.ThreatActive) s.ThreatCrashed = true;
                s.CleanFollowHoldSec = 0f;
            }
            _accum[id] = s;
        }

        /// <summary>
        /// Per-tick threat-cone scan via the shared
        /// <see cref="RacingObservationLayout.TryFindPeakThreatClosing"/> helper.
        /// Returns true while any opponent is inside the close-call cone (drives
        /// tire waiver); emits the avoidance bonus on clear.
        /// </summary>
        private bool UpdateThreatState(CarId carId, ref AccumState s, float dt)
        {
            if (_sim == null || !_sim.TryGetState(carId, out var self))
                return false;

            int activeCount = Mathf.Max(1, _sim.ActiveCars.Count);
            Span<RacingObservationLayout.OtherCar> others =
                stackalloc RacingObservationLayout.OtherCar[activeCount];
            int n = 0;
            foreach (var id in _sim.ActiveCars)
            {
                if (id.Value == carId.Value) continue;
                if (!_sim.TryGetState(id, out var os)) continue;
                if (n >= others.Length) break;
                others[n++] = new RacingObservationLayout.OtherCar(
                    id, os.Position, os.Heading, os.VelocityXZ.magnitude, os.VelocityXZ);
            }

            bool inCone = RacingObservationLayout.TryFindPeakThreatClosing(
                self.Position, self.Heading, self.VelocityXZ,
                others.Slice(0, n),
                _threatRayMaxMeters,
                RacingObservationLayout.ConeHalfAngleRad,
                _threatClosingMinMS,
                out float peakClosing);

            float selfSpeed = self.VelocityXZ.magnitude;

            if (inCone)
            {
                s.ThreatActive = true;
                s.ThreatActiveSec += dt;
                s.ThreatClearStreakSec = 0f;
                if (peakClosing > s.ThreatPeakClosingMS) s.ThreatPeakClosingMS = peakClosing;

                if (s.EpisodeElapsed >= _gridGraceSeconds &&
                    s.ThreatActiveSec >= _threatPatienceWindowSec &&
                    selfSpeed < _stuckSpeedThresholdMS)
                {
                    float aggressionMul = 0.5f + 1.5f * EffectivePassingAggression(carId);
                    s.PendingReward -= _stuckInThreatPenaltyPerSec * aggressionMul * dt;
                }
                return true;
            }

            if (s.ThreatActive)
            {
                s.ThreatClearStreakSec += dt;
                if (s.ThreatClearStreakSec >= _threatClearWindowSec)
                {
                    if (!s.ThreatCrashed && selfSpeed >= _minAvoidanceClearSpeedMS)
                    {
                        float magnitude = Mathf.Clamp01(s.ThreatPeakClosingMS / _avoidanceClosingCapMS);
                        float aggressionMul = 0.5f + 2.0f * EffectivePassingAggression(carId);
                        s.PendingReward += _avoidanceCoeff * magnitude * aggressionMul;
                    }
                    s.ThreatActive = false;
                    s.ThreatActiveSec = 0f;
                    s.ThreatPeakClosingMS = 0f;
                    s.ThreatClearStreakSec = 0f;
                    s.ThreatCrashed = false;
                }
            }
            return false;
        }

        private void OnFuelOut(FuelDepletedEvent e)
        {
            if (_active != null && !_active.Has(StageFeature.FuelOutTerminal)) return;

            if (!_accum.TryGetValue(e.Id, out var s)) s = new AccumState();
            var prof = PersonalityOf(e.Id);
            s.PendingReward -= _fuelOutPenalty * (0.5f + prof.FuelEconomy);
            s.PendingEnd = EpisodeEndReason.Failure_FuelOut;
            _accum[e.Id] = s;
        }

        private void OnPuncture(TirePuncturedEvent e)
        {
            if (!_accum.TryGetValue(e.Id, out var s)) s = new AccumState();
            s.PunctureCount++;
            s.SectorCleanCurrent = false;
            _accum[e.Id] = s;
        }

        private void OnOffTrack(CarOffTrackEvent e)
        {
            if (_accum.TryGetValue(e.Id, out var sClean))
            {
                sClean.SectorCleanCurrent = false;
                _accum[e.Id] = sClean;
            }

            if (_active != null && !_active.Has(StageFeature.PunctureOffTrackTerminal)) return;

            if (!_accum.TryGetValue(e.Id, out var s)) return;
            if (s.PunctureCount >= 2)
            {
                s.PendingReward -= _punctureOffTrackPenalty;
                s.PendingEnd = EpisodeEndReason.Failure_PuncturedAndOffTrack;
                _accum[e.Id] = s;
            }
        }

        private struct AccumState
        {
            public float PendingReward;
            public EpisodeEndReason? PendingEnd;
            public int PunctureCount;
            public DriverPersonality Personality;
            public float CleanCooldownSec;
            public float EpisodeElapsed;
            public float RearEndCooldownSec;
            public float OvertakeGraceSec;
            public bool SectorCleanCurrent;
            public bool SectorCleanAwarded;
            public bool ThreatActive;
            public float ThreatActiveSec;
            public float ThreatPeakClosingMS;
            public float ThreatClearStreakSec;
            public bool ThreatCrashed;
            public bool InFirstSector;
            public float[] BestSectorTime;
            public float LastSectorCrossAtSec;
            public float CleanFollowHoldSec;
            public float RecentDraftPeak;
            public float LapStartElapsedSec;
            public int LastMicroIndex;
            public int CachedMicroCount;
            public float PaceShapingAccumThisLap;
            public int LapCount;
        }

        private struct OvertakePending
        {
            public CarId Passed;
            public int SectorsHeld;
        }
    }
}
