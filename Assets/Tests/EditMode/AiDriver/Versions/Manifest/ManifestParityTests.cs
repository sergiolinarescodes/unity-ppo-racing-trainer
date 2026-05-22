using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityPpoRacingTrainer.Core.AiDriver.Physics;
using UnityPpoRacingTrainer.Core.AiDriver.Policy;
using UnityPpoRacingTrainer.Core.AiDriver.Training;
using UnityPpoRacingTrainer.Core.AiDriver.Training.Stages;
using UnityPpoRacingTrainer.Core.AiDriver.Versions;
using UnityPpoRacingTrainer.Core.AiDriver.Versions.Manifest;

namespace UnityPpoRacingTrainer.Core.Tests.AiDriver.Versions.Manifest
{
    /// <summary>
    /// Verifies that the canonical manifests under
    /// <c>Assets/_Bootstrap/Configs/Versions/*.json</c> are well-formed and
    /// don't silently drift on a future edit. The manifest is the source of
    /// truth — these tests pin the shapes a human edit could break (frozen
    /// observation layout, stage curriculum content, drafting defaults) so
    /// the next person who changes the JSON sees the test fail before the
    /// trainer does.
    /// </summary>
    [TestFixture]
    public class ManifestParityTests
    {
        private VersionManifest _latest;
        private VersionManifest _v1;

        [SetUp]
        public void Setup()
        {
            var dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..",
                VersionManifestLoader.DefaultRelativeFolder));
            _latest = LoadOrSkip(dir, "latest.json");
            _v1 = LoadOrSkip(dir, "v1.json");
        }

        private static VersionManifest LoadOrSkip(string dir, string fileName)
        {
            var path = Path.Combine(dir, fileName);
            Assume.That(File.Exists(path), $"{fileName} missing at {path}; manifest fixture not in place.");
            var manifest = VersionManifestLoader.TryLoad(path);
            Assert.That(manifest, Is.Not.Null, $"{fileName} failed to parse.");
            return manifest;
        }

        [Test]
        public void Latest_VersionId_And_DisplayName()
        {
            Assert.That(_latest.VersionId, Is.EqualTo("latest"));
            Assert.That(_latest.DisplayName, Is.EqualTo("Latest"));
            Assert.That(_latest.SchemaVersion, Is.EqualTo(1));
        }

        [Test]
        public void V1_VersionId_And_DisplayName()
        {
            Assert.That(_v1.VersionId, Is.EqualTo("v1"));
            Assert.That(_v1.SchemaVersion, Is.EqualTo(1));
        }

        [Test]
        public void Latest_MlAgents_And_Runtime()
        {
            Assert.That(_latest.MlAgents.BehaviorName, Is.EqualTo("RacingDriver"));
            Assert.That(_latest.MlAgents.ConfigPath,
                Is.EqualTo("Assets/_Bootstrap/Configs/MlAgents/racing_driver.yaml"));
            Assert.That(_latest.Runtime.PrefabResourcePath, Is.EqualTo("AiDriver/AiDriverAgent"));
            Assert.That(_latest.Runtime.OnnxResourcePath, Is.EqualTo("latest"));
            Assert.That(_latest.Runtime.RequiresSideSystems, Is.True);
        }

        [Test]
        public void V1_MlAgents_And_Runtime_Are_The_Snapshot_Triplet()
        {
            Assert.That(_v1.MlAgents.BehaviorName, Is.EqualTo("RacingDriverV1"));
            Assert.That(_v1.Runtime.PrefabResourcePath, Is.EqualTo("AiDriver/Legacy/AiDriverAgentV1"));
            Assert.That(_v1.Runtime.OnnxResourcePath, Is.EqualTo("AiDriver/Policies/RacingDriver-v1"));
        }

        [Test]
        public void Latest_PhysicsDefaults_Match_Canonical_CSharp()
        {
            // The C# constant AiDriverPhysicsDefaults.Latest is kept as the
            // "physics live alongside the trainer" fallback — when the
            // manifest is missing, this is what the trainer would use. The
            // ManifestBackedVersionProfile.BuildPhysics() applies the same
            // cell-size scaling. They must match bit-for-bit.
            var profile = new ManifestBackedVersionProfile(_latest,
                () => NullRewardShaper.Instance);
            Assert.That(profile.PhysicsDefaults, Is.EqualTo(AiDriverPhysicsDefaults.Latest),
                "latest.json physics drifted from AiDriverPhysicsDefaults.Latest. " +
                "Sync the two — the C# constant is the source of truth for the canonical.");
        }

        [Test]
        public void Latest_Stages_Are_The_Six_Curriculum_Stages()
        {
            var profile = new ManifestBackedVersionProfile(_latest,
                () => NullRewardShaper.Instance);
            var registry = profile.StageProfiles;

            // Hand-coded feature bitmasks from the original C# StageProfiles.cs
            // (deleted in Phase 4). These are the snapshot of "what was true at
            // the time we migrated to manifest-driven versioning". Edits here
            // are explicit and visible in code review.
            AssertStage(registry, 0, "Stage0SoloWarmup",
                StageFeature.TireOverstressPenalty | StageFeature.TireObservations,
                expectedOpponents: 0, fuel: FuelSamplingMode.Abundant, personality: PersonalitySamplingMode.Uniform);
            AssertStage(registry, 1, "Stage1Grid",
                StageFeature.OvertakeReward | StageFeature.GotPassedPenalty | StageFeature.MicroSectorPositionBonus
                    | StageFeature.LapPositionBonus | StageFeature.CarHitCarPenalty | StageFeature.DraftBonus
                    | StageFeature.CleanDrivingBonus | StageFeature.HoldPositionBonus | StageFeature.OpponentObservations
                    | StageFeature.TireOverstressPenalty | StageFeature.TireObservations,
                expectedOpponents: 11, fuel: FuelSamplingMode.Abundant, personality: PersonalitySamplingMode.Uniform);
            AssertStage(registry, 2, "Stage2FuelScarcity",
                StageFeature.OvertakeReward | StageFeature.GotPassedPenalty | StageFeature.MicroSectorPositionBonus
                    | StageFeature.LapPositionBonus | StageFeature.CarHitCarPenalty | StageFeature.DraftBonus
                    | StageFeature.CleanDrivingBonus | StageFeature.HoldPositionBonus
                    | StageFeature.FuelMarginPenalty | StageFeature.FuelOutTerminal
                    | StageFeature.OpponentObservations | StageFeature.FuelObservations
                    | StageFeature.TireOverstressPenalty | StageFeature.TireObservations,
                expectedOpponents: 11, fuel: FuelSamplingMode.Scarcity, personality: PersonalitySamplingMode.Uniform);
            AssertStage(registry, 3, "Stage3TireFuel",
                StageFeature.OvertakeReward | StageFeature.GotPassedPenalty | StageFeature.MicroSectorPositionBonus
                    | StageFeature.LapPositionBonus | StageFeature.CarHitCarPenalty | StageFeature.DraftBonus
                    | StageFeature.CleanDrivingBonus | StageFeature.HoldPositionBonus
                    | StageFeature.FuelMarginPenalty | StageFeature.FuelOutTerminal
                    | StageFeature.TireOverstressPenalty | StageFeature.PunctureOffTrackTerminal
                    | StageFeature.OpponentObservations | StageFeature.FuelObservations | StageFeature.TireObservations,
                expectedOpponents: 11, fuel: FuelSamplingMode.Scarcity, personality: PersonalitySamplingMode.Uniform);
            AssertStage(registry, 4, "Stage4AuthoredTwoCar",
                StageFeature.OvertakeReward | StageFeature.GotPassedPenalty | StageFeature.MicroSectorPositionBonus
                    | StageFeature.LapPositionBonus | StageFeature.CarHitCarPenalty | StageFeature.DraftBonus
                    | StageFeature.CleanDrivingBonus | StageFeature.HoldPositionBonus
                    | StageFeature.FuelMarginPenalty | StageFeature.FuelOutTerminal
                    | StageFeature.TireOverstressPenalty | StageFeature.PunctureOffTrackTerminal
                    | StageFeature.OpponentObservations | StageFeature.FuelObservations | StageFeature.TireObservations
                    | StageFeature.PersonalityObservations,
                expectedOpponents: 11, fuel: FuelSamplingMode.Scarcity, personality: PersonalitySamplingMode.Archetype);
            AssertStage(registry, 5, "Stage5PackSelfPlay",
                StageFeature.OvertakeReward | StageFeature.GotPassedPenalty | StageFeature.MicroSectorPositionBonus
                    | StageFeature.LapPositionBonus | StageFeature.CarHitCarPenalty | StageFeature.DraftBonus
                    | StageFeature.CleanDrivingBonus | StageFeature.HoldPositionBonus
                    | StageFeature.FuelMarginPenalty | StageFeature.FuelOutTerminal
                    | StageFeature.TireOverstressPenalty
                    | StageFeature.OpponentObservations | StageFeature.FuelObservations | StageFeature.TireObservations
                    | StageFeature.PersonalityObservations | StageFeature.FrontConeRayObservations,
                expectedOpponents: 11, fuel: FuelSamplingMode.Scarcity, personality: PersonalitySamplingMode.Archetype);
        }

        [Test]
        public void Latest_FloatsPerFrame_Equals_Sixty()
        {
            Assert.That(_latest.Observation.FloatsPerFrame, Is.EqualTo(60),
                "Layout is frozen per ONNX. Bumping requires re-training and a new snapshot.");
        }

        [Test]
        public void Latest_LayoutHash_Matches_RacingObservationLayout()
        {
            // Couples the manifest's observation.layoutHash to the live
            // RacingObservationLayout constants. If either side drifts (an
            // editor changes the wall ray angles in code, or someone edits
            // the manifest by hand), this test points at the source of truth
            // (the ObservationWriter) and the regenerate-and-snapshot recipe.
            var expected = RacingObservationLayout.ComputeLayoutHash();
            Assert.That(_latest.Observation.LayoutHash, Is.EqualTo(expected),
                "latest.json observation.layoutHash drifted from RacingObservationLayout. " +
                "If the layout change is intentional, snapshot the previous canonical " +
                "to a frozen <id>.json first, THEN bake the new hash into latest.json.");
        }

        [Test]
        public void V1_LayoutHash_Matches_Snapshot()
        {
            // V1 was snapshotted before any layout edits, so its hash must
            // match Latest's at the time the snapshot was taken. The day
            // someone bumps the canonical layout, this test will need a real
            // historical hash literal here — V1's recorded hash, NOT the
            // current live one.
            var live = RacingObservationLayout.ComputeLayoutHash();
            Assert.That(_v1.Observation.LayoutHash, Is.EqualTo(live),
                "v1.json observation.layoutHash diverged from live layout. " +
                "If the canonical layout has since changed, replace this assertion " +
                "with the V1 hash literal — V1 is a snapshot, not a moving target.");
        }

        [Test]
        public void Latest_Drafting_Matches_Historical_Defaults()
        {
            // DraftingSettings init defaults mirror the historical baked
            // constants from Drafting.cs (8 / 2.5 / 0.33 / 0.09625 / 6 / 3 /
            // 0.05 / 0.7). The manifest's drafting section must match — any
            // drift here silently rewrites slipstream behavior.
            Assert.That(_latest.Drafting, Is.EqualTo(new DraftingSettings()),
                "Drafting settings drift between latest.json and DraftingSettings init defaults. " +
                "Bring them back in sync or bump a snapshot.");
        }

        [Test]
        public void Loader_Returns_Latest_And_V1()
        {
            var all = VersionManifestLoader.LoadAll();
            Assert.That(all.ContainsKey("latest"), Is.True, "LoadAll missing 'latest'.");
            Assert.That(all.ContainsKey("v1"), Is.True, "LoadAll missing 'v1'.");
        }

        private static void AssertStage(IStageProfileRegistry registry, int id, string name,
            StageFeature expectedFeatures, int expectedOpponents,
            FuelSamplingMode fuel, PersonalitySamplingMode personality)
        {
            Assert.That(registry.Has(id), Is.True, $"manifest stages array missing id {id}.");
            var actual = registry.Get(id);
            Assert.That(actual.Name, Is.EqualTo(name), $"stage {id} name drift.");
            Assert.That(actual.Features, Is.EqualTo(expectedFeatures),
                $"stage {id} feature bitmask drift: expected {expectedFeatures}, got {actual.Features}");
            Assert.That(actual.ExpectedOpponentCount, Is.EqualTo(expectedOpponents),
                $"stage {id} ExpectedOpponentCount drift.");
            Assert.That(actual.Fuel, Is.EqualTo(fuel), $"stage {id} fuel sampling drift.");
            Assert.That(actual.Personality, Is.EqualTo(personality),
                $"stage {id} personality sampling drift.");
        }
    }
}
