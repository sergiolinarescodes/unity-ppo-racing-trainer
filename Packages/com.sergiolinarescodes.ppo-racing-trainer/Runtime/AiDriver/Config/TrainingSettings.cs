using System.Collections.Generic;

namespace UnityPpoRacingTrainer.Core.AiDriver.Config
{
    /// <summary>
    /// POCO that mirrors <c>settings.json</c> at the repo root. Loaded once at
    /// <see cref="ITrainingSettingsService"/> resolution; missing fields fall
    /// back to the baked defaults declared on each property here. Partial JSON
    /// files merge over defaults — never throws on missing fields.
    /// </summary>
    public sealed record TrainingSettings
    {
        public int SchemaVersion { get; init; } = 1;

        public EpisodeSettings Episode { get; init; } = new();
        public PhysicsSettings Physics { get; init; } = new();
        public TirePhysicsSettings TirePhysics { get; init; } = new();
        public RewardShaperSettings RewardShaper { get; init; } = new();
        public TrackGeometrySettings TrackGeometry { get; init; } = new();
        public ObservationSettings Observation { get; init; } = new();
    }

    public sealed record EpisodeSettings
    {
        public int MaxStepsPerEpisode { get; init; } = 12000;
        public float OffTrackTimeoutSec { get; init; } = 0.5f;
    }

    /// <summary>
    /// Per-car physics tunings. Values are in cell-relative units where noted —
    /// <c>maxAccel: 1.74</c> means <c>1.74 * carPhysicsCellSize</c> at runtime;
    /// do not pre-multiply in the JSON.
    /// </summary>
    public sealed record PhysicsSettings
    {
        public float WheelBase { get; init; } = 0.3f;
        public float MaxSteer { get; init; } = 0.45f;
        public float MaxAccel { get; init; } = 1.74f;
        public float MaxBrake { get; init; } = 2.0f;
        public float MaxSpeed { get; init; } = 7.5f;
        public float DragCoefficient { get; init; } = 0.5f;
        public float SteerRate { get; init; } = 1.2f;
        public float Gravity { get; init; } = 9.81f;
        public float BoostThrust { get; init; } = 6f;
        public float BoostDurationSec { get; init; } = 1.5f;
        public float BoostRechargeRate { get; init; } = 0.2f;
        public float OffTrackDragMul { get; init; } = 5f;
        public float OffTrackSpeedCapFrac { get; init; } = 0.30f;
        public float LateralGripFactor { get; init; } = 21f;
        public float OffTrackGripFactor { get; init; } = 6f;
        public float SlipReleaseFactor { get; init; } = 0.25f;
        public float SpeedInducedUndersteerGain { get; init; } = 20.0f;
        public float MinCruiseSpeed { get; init; } = 0f;
        public float LowSpeedTurnBonus { get; init; } = 1.0f;
        public float KerbGripFactor { get; init; } = 45f;
        public float WallBounceDamping { get; init; } = 0.10f;
        public float WallNormalRestitution { get; init; } = 0.15f;
        public float CarCollisionRadius { get; init; } = 0.09f;
        public float OffKerbCorneringPenalty { get; init; } = 0.30f;
        public float OffKerbCorneringSteerThreshold { get; init; } = 0.20f;
        public float WallStunSeconds { get; init; } = 0.1f;
        public float WallDamageCoefficient { get; init; } = 0.08f;
        public float WallDamageMinPerHit { get; init; } = 0.02f;
        public float MinHealthSpeedFactor { get; init; } = 0.30f;
        public float WallStunSecondsPerImpactSpeed { get; init; } = 0.04f;
        public float MaxStunSeconds { get; init; } = 0.6f;
        public float TractionCircleGain { get; init; } = 8.0f;
        public float MinSteerAuthority { get; init; } = 0.18f;
        public float HighSpeedSteerRateFactor { get; init; } = 3.5f;
        public float SpeedLateralGripScale { get; init; } = 14.0f;
        public float TrailBrakeAuthorityWeight { get; init; } = 0.15f;
        public float StraightLineAeroBoost { get; init; } = 1.60f;
        public float StraightLineAeroRampSec { get; init; } = 1.2f;
        public float StraightLineAeroRecoverySec { get; init; } = 0.3f;
        public float StraightLineAeroSteerThreshold { get; init; } = 0.10f;
    }

    public sealed record TirePhysicsSettings
    {
        public float KLateralG { get; init; } = 0.001512f;
        public float KSlip { get; init; } = 0.011347f;
        public float KBurnout { get; init; } = 0.01296f;
        public float KBrake { get; init; } = 0.006048f;
        public float HardBrakeThreshold { get; init; } = 0.9f;
        public float HardBrakePeakMul { get; init; } = 5.0f;
        public float HardBrakeExponent { get; init; } = 6.0f;
        public float BrakeBlockadeInputThreshold { get; init; } = 0.99f;
        public float BrakeBlockadeHoldSeconds { get; init; } = 0.2f;
        public float BrakeBlockadePenaltyMul { get; init; } = 5.0f;
        public float KKerbStress { get; init; } = 1.5f;
        public float PunctureThreshold { get; init; } = 0.97f;
        public float PunctureGThreshold { get; init; } = 6.0f;
        public float PunctureBaseChancePerSec { get; init; } = 0.0f;
        public float PuncturedGripFactor { get; init; } = 0.10f;
    }

    public sealed record RewardShaperSettings
    {
        public RewardOvertakeSettings Overtakes { get; init; } = new();
        public RewardPositionSettings Position { get; init; } = new();
        public RewardContactSettings Contact { get; init; } = new();
        public RewardThreatSettings Threat { get; init; } = new();
        public RewardPackSettings Pack { get; init; } = new();
        public RewardPaceSettings Pace { get; init; } = new();
        public RewardDraftSettings DraftAndConsumables { get; init; } = new();
    }

    public sealed record RewardOvertakeSettings
    {
        public float OvertakeBonus { get; init; } = 4.6f;
        public float OvertakeHoldPerSectorBase { get; init; } = 5.175f;
        public int OvertakeHoldSectorCap { get; init; } = 16;
        public float OvertakeFullLapHeldBonus { get; init; } = 69.0f;
        public float GotPassedPenalty { get; init; } = 3.75f;
        public float GridGraceSeconds { get; init; } = 4.0f;
        public float MinPassingAggression { get; init; } = 0.5f;
        public float FirstSectorAggressionMin { get; init; } = 0.56f;
        public float FirstSectorAggressionMax { get; init; } = 0.76f;
    }

    public sealed record RewardPositionSettings
    {
        public float PassivePositionPerSectorScale { get; init; } = 2.07f;
        public float PassivePositionPerLapScale { get; init; } = 20.7f;
        public float CleanDrivingBonusPerSec { get; init; } = 0.66f;
        public float CleanDrivingWindowSec { get; init; } = 3.0f;
        public float CleanRaceBonusTerminal { get; init; } = 7.5f;
        public float HoldPositionBonusPerSec { get; init; } = 0.15f;
        public float SectorCleanBonus { get; init; } = 1.5f;
        public float SectorCleanHealthThreshold { get; init; } = 0.9f;
    }

    public sealed record RewardContactSettings
    {
        public float CarCrashCoef { get; init; } = 1.8f;
        public float RearEndOffenderMul { get; init; } = 5.0f;
        public float RearEndOffenderFlatPenalty { get; init; } = 50.0f;
        public float LowHpVictimThreshold { get; init; } = 0.3f;
        public float LowHpVictimExtraPenalty { get; init; } = 80.0f;
        public float DestroyVictimExtraPenalty { get; init; } = 200.0f;
        public float DestroyedHealthEpsilon { get; init; } = 0.01f;
        public float RearEndVictimMul { get; init; } = 0.1f;
        public float RearEndCooldownSec { get; init; } = 0.5f;
        public float OvertakeGraceSec { get; init; } = 1.0f;
        public float MinContactImpactForFlatPenaltyMS { get; init; } = 0.0f;
    }

    public sealed record RewardThreatSettings
    {
        public float RayMaxMeters { get; init; } = 8.0f;
        public float ClosingMinMS { get; init; } = 1.5f;
        public float ClearWindowSec { get; init; } = 0.5f;
        public float TireWaiverScale { get; init; } = 0.25f;
        public float PatienceWindowSec { get; init; } = 0.2f;
        public float AvoidanceCoeff { get; init; } = 0.25f;
        public float AvoidanceClosingCapMS { get; init; } = 4.0f;
        public float MinAvoidanceClearSpeedMS { get; init; } = 1.5f;
        public float StuckInThreatPenaltyPerSec { get; init; } = 1.0f;
        public float StuckSpeedThresholdMS { get; init; } = 1.5f;
    }

    public sealed record RewardPackSettings
    {
        public float PackProximityBonusPerSec { get; init; } = 0.30f;
        public float PackProximityRadiusM { get; init; } = 5.0f;
        public int PackProximityCarCountCap { get; init; } = 6;
        public float PackProximityMinSpeedFrac { get; init; } = 0.35f;
        public float CleanFollowBonusPerSec { get; init; } = 0.60f;
        public float CleanFollowMaxDistM { get; init; } = 3.0f;
        public float CleanFollowMinDistM { get; init; } = 0.50f;
        public float CleanFollowMinHoldSec { get; init; } = 1.0f;
        public float CleanFollowMaxHoldSec { get; init; } = 4.0f;
        public int PackRacingMinDriverCount { get; init; } = 6;
    }

    public sealed record RewardPaceSettings
    {
        public float BeatBestLapBonus { get; init; } = 1.50f;
        public float MatchBestLapBonus { get; init; } = 0.60f;
        public float MatchBestLapToleranceFrac { get; init; } = 0.01f;
        public float PaceShapingPerStep { get; init; } = 0.0040f;
        public float PaceShapingMaxPerLap { get; init; } = 0.50f;
        public float PaceProjectionMinArcFrac { get; init; } = 0.05f;
        public float PaceProjectionMargin { get; init; } = 0.015f;
        public float PeakPaceMinMultiplier { get; init; } = 0.10f;
        public float PeakPaceMaxMultiplier { get; init; } = 1.75f;
        public float PeakPaceSectorImproveCoeff { get; init; } = 0.5f;
        public float PeakPaceSectorEmaAlpha { get; init; } = 0.15f;
        public float PeakPaceSectorMaxAbsDeltaSec { get; init; } = 1.0f;
    }

    public sealed record RewardDraftSettings
    {
        public float DraftBonusPerSec { get; init; } = 0.4f;
        public float DraftPassBonus { get; init; } = 25.0f;
        public float DraftPassMinStrength { get; init; } = 0.5f;
        public float DraftPassLookbackSec { get; init; } = 1.0f;
        public float FuelMarginPenaltyPerSec { get; init; } = 0.4f;
        public float TireOverstressPenaltyPerSec { get; init; } = 0.3f;
        public float ReferenceFuelBurnPerLap { get; init; } = 25.0f;
        public float FuelOutPenalty { get; init; } = 50.0f;
        public float PunctureOffTrackPenalty { get; init; } = 30.0f;
    }

    /// <summary>
    /// Track geometry. Some fields (CellSize, CarPhysicsCellSize) are
    /// referenced inside Burst jobs as compile-time consts; runtime overrides
    /// are read by non-Burst readers but ignored by the Burst-jobs path.
    /// </summary>
    public sealed record TrackGeometrySettings
    {
        public float CellSize { get; init; } = 3.0f;
        public float CarPhysicsCellSize { get; init; } = 2.0f;
        public float LaneHalfWidth { get; init; } = 0.405f;
        public float WallHeight { get; init; } = 0.40f;
        public float WallThickness { get; init; } = 0.06f;
        public float WallShoulderNear { get; init; } = 0.04f;
        public float WallShoulderMid { get; init; } = 0.18f;
        public float WallShoulder { get; init; } = 0.075f;
        public float RampRise { get; init; } = 0.5f;
        public float CurveRadiusSmall { get; init; } = 0.5f;
        public float CurveRadiusLarge { get; init; } = 1.0f;
        public int CurveSegmentsSmall { get; init; } = 10;
        public int CurveSegmentsLarge { get; init; } = 16;
        public float PianoBandWidth { get; init; } = 0.06f;
    }

    /// <summary>
    /// Observation layout — FROZEN per ONNX version. Exposed for inspection
    /// only; mutation is ignored at load time and rejected by the dashboard.
    /// </summary>
    public sealed record ObservationSettings
    {
        public int FloatsPerFrame { get; init; } = 60;
        public int StackedFrames { get; init; } = 3;
        public int WallRayCount { get; init; } = 7;
        public float WallRayMaxMeters { get; init; } = 16.0f;
        public int LookaheadAnchors { get; init; } = 5;
        public IReadOnlyList<float> LookaheadSeconds { get; init; } = new[] { 0.0f, 0.1f, 0.3f, 0.7f, 1.5f };
        public int OpponentRayCount { get; init; } = 5;
        public float ConeHalfAngleRad { get; init; } = 0.7854f;
        public int OtherCarsAhead { get; init; } = 2;
        public int OtherCarsBehind { get; init; } = 2;
        public float LateralGAtSaturation { get; init; } = 8.0f;
        public float ReferenceHalfWidth { get; init; } = 1.5f;

        /// <summary>
        /// Stable FNV-1a 64-bit hex hash of the observation layout structure
        /// (FloatsPerFrame, block sizes, wall ray angles, lookahead seconds).
        /// Empty string means "no hash recorded" (legacy / unmigrated
        /// manifest); the snapshot test then dumps the live value so the
        /// maintainer can paste it in.
        ///
        /// Distinct from runtime float values — only the structure is
        /// hashed. Drift detection without re-training paranoia.
        /// </summary>
        public string LayoutHash { get; init; } = "";
    }
}
