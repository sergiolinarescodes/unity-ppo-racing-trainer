using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityPpoRacingTrainer.Core.AiDriver.Physics;
using UnityPpoRacingTrainer.Core.AiDriver.Training.Stages;
using UnityPpoRacingTrainer.Core.AiDriver.Versions;
using UnityPpoRacingTrainer.Core.AiDriver.Versions.Latest;
using UnityPpoRacingTrainer.Core.AiDriver.Versions.Manifest;

namespace UnityPpoRacingTrainer.Core.Tests.AiDriver.Versions.Manifest
{
    /// <summary>
    /// Phase 1 parity scaffold. Locks the contract that the manifest at
    /// <c>Assets/_Bootstrap/Configs/Versions/latest.json</c> reproduces the
    /// current C# <c>LatestVersionProfile</c> field-for-field. When phase 2+
    /// flip the active-profile binding to the manifest, this test is the
    /// guarantee that no observable behavior shifts.
    ///
    /// If a future contributor edits <c>AiDriverPhysicsDefaults.Latest</c> or
    /// <c>StageProfiles.cs</c> without mirroring the change into
    /// <c>latest.json</c> (or vice versa), this test fails and they get
    /// pointed back at this comment.
    /// </summary>
    [TestFixture]
    public class ManifestParityTests
    {
        private VersionManifest _latest;

        [SetUp]
        public void Setup()
        {
            var dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..",
                VersionManifestLoader.DefaultRelativeFolder));
            var path = Path.Combine(dir, "latest.json");
            Assume.That(File.Exists(path), $"latest.json missing at {path}; phase 1 fixture not in place.");
            _latest = VersionManifestLoader.TryLoad(path);
            Assert.That(_latest, Is.Not.Null, "latest.json failed to parse.");
        }

        [Test]
        public void Latest_VersionId_And_DisplayName()
        {
            Assert.That(_latest.VersionId, Is.EqualTo("latest"));
            Assert.That(_latest.DisplayName, Is.EqualTo("Latest"));
            Assert.That(_latest.SchemaVersion, Is.EqualTo(1));
        }

        [Test]
        public void Latest_MlAgents_And_Runtime_Match_LatestVersionProfile()
        {
            Assert.That(_latest.MlAgents.BehaviorName, Is.EqualTo("RacingDriver"));
            Assert.That(_latest.MlAgents.ConfigPath,
                Is.EqualTo("Assets/_Bootstrap/Configs/MlAgents/racing_driver.yaml"));
            Assert.That(_latest.Runtime.PrefabResourcePath, Is.EqualTo("AiDriver/AiDriverAgent"));
            Assert.That(_latest.Runtime.OnnxResourcePath, Is.EqualTo("latest"));
            Assert.That(_latest.Runtime.RequiresSideSystems, Is.True);
        }

        [Test]
        public void Latest_PhysicsDefaults_Match_AiDriverPhysicsDefaults_Latest()
        {
            // Build via the same path the dormant profile would use, then
            // compare element-by-element to the C# constant the trainer
            // actually consumes today.
            var profile = new ManifestBackedVersionProfile(_latest,
                rewardShaperFactory: () => UnityPpoRacingTrainer.Core.AiDriver.Versions.Latest.NullRewardShaper.Instance,
                versionEnum: AiDriverVersion.Latest);
            var fromManifest = profile.PhysicsDefaults;
            var fromCode = AiDriverPhysicsDefaults.Latest;
            Assert.That(fromManifest, Is.EqualTo(fromCode),
                "Manifest physics drifted from AiDriverPhysicsDefaults.Latest. " +
                "Sync latest.json + AiDriverPhysicsDefaults.Latest, or this " +
                "becomes a silent regression when phase 2 flips the switch.");
        }

        [Test]
        public void Latest_Stages_Match_StageProfiles_Cs()
        {
            var profile = new ManifestBackedVersionProfile(_latest,
                rewardShaperFactory: () => UnityPpoRacingTrainer.Core.AiDriver.Versions.Latest.NullRewardShaper.Instance,
                versionEnum: AiDriverVersion.Latest);
            var registry = profile.StageProfiles;

            AssertStageMatches(registry, 0, new Stage0SoloWarmupProfile());
            AssertStageMatches(registry, 1, new Stage1GridProfile());
            AssertStageMatches(registry, 2, new Stage2FuelScarcityProfile());
            AssertStageMatches(registry, 3, new Stage3TireFuelProfile());
            AssertStageMatches(registry, 4, new Stage4AuthoredTwoCarProfile());
            AssertStageMatches(registry, 5, new Stage5PackSelfPlayProfile());
        }

        [Test]
        public void Latest_FloatsPerFrame_Equals_Sixty()
        {
            Assert.That(_latest.Observation.FloatsPerFrame, Is.EqualTo(60),
                "Layout is frozen per ONNX. Bumping requires re-training and a new snapshot.");
        }

        [Test]
        public void Latest_Drafting_Matches_LatestVersionProfile_Defaults()
        {
            // Phase 2 flips DraftService from baked constants to per-version
            // injected DraftingSettings. LatestVersionProfile.Drafting returns
            // `new DraftingSettings()` — the init defaults must reproduce the
            // historical baked values (8 / 2.5 / 0.33 / 0.09625 / 6 / 3 /
            // 0.05 / 0.7). If anyone changes the defaults without bumping
            // the snapshot, this test points them back here.
            var manifestDrafting = _latest.Drafting;
            var legacy = new LatestVersionProfile(
                () => UnityPpoRacingTrainer.Core.AiDriver.Versions.Latest.NullRewardShaper.Instance).Drafting;
            Assert.That(manifestDrafting, Is.EqualTo(legacy),
                "DraftingSettings drift between latest.json and LatestVersionProfile defaults. " +
                "DraftService injects this directly; a mismatch silently changes slipstream behavior.");
        }

        [Test]
        public void Loader_Returns_Latest()
        {
            var all = VersionManifestLoader.LoadAll();
            Assert.That(all.ContainsKey("latest"), Is.True,
                "LoadAll should pick up latest.json by version_id.");
        }

        private static void AssertStageMatches(IStageProfileRegistry registry, int id, IStageProfile expected)
        {
            Assert.That(registry.Has(id), Is.True, $"manifest stages array missing id {id}.");
            var actual = registry.Get(id);
            Assert.That(actual.Features, Is.EqualTo(expected.Features),
                $"stage {id} feature bitmask drift: expected {expected.Features}, got {actual.Features}");
            Assert.That(actual.ExpectedOpponentCount, Is.EqualTo(expected.ExpectedOpponentCount),
                $"stage {id} ExpectedOpponentCount drift.");
            Assert.That(actual.Fuel, Is.EqualTo(expected.Fuel), $"stage {id} fuel sampling drift.");
            Assert.That(actual.Personality, Is.EqualTo(expected.Personality),
                $"stage {id} personality sampling drift.");
        }
    }
}
