using System.Text;
using NUnit.Framework;
using UnityPpoRacingTrainer.Core.AiDriver.Policy;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Tests.AiDriver.Policy
{
    /// <summary>
    /// Structural snapshot of <see cref="RacingObservationLayout"/>. A trained
    /// ONNX policy is shape-locked to the float count and per-index meaning of
    /// this layout; any drift here silently breaks every previously-trained
    /// model. These tests are the cheap line of defence — they fail at compile
    /// /test time the moment a contributor edits one constant without updating
    /// the matching block.
    ///
    /// If you legitimately want to change the observation, see
    /// <c>docs/observation-versioning.md</c>: freeze the current canonical
    /// under <c>Versions/V&lt;N&gt;/</c>, *then* edit <c>Latest</c> and update
    /// the golden values here.
    /// </summary>
    [TestFixture]
    public class ObservationLayoutSnapshotTests
    {
        // -- Golden floats per frame. Bumping any of these is a BREAKING change
        //    for previously-trained ONNX. Make sure docs/observation-versioning.md
        //    reflects the bump.
        private const int GoldenFloatsPerFrame = 60;
        private const int GoldenBaseObservationFloats = 25;
        private const int GoldenRaceContextFloats = 25;
        private const int GoldenFrontConeFloats = 10;

        [Test]
        public void FloatsPerFrame_MatchesGolden()
        {
            Assert.That(RacingObservationLayout.FloatsPerFrame, Is.EqualTo(GoldenFloatsPerFrame),
                "Trained ONNX expects a fixed observation size. If you changed " +
                "FloatsPerFrame deliberately, snapshot the previous Latest under " +
                "Versions/V<N>/ before retraining. See docs/observation-versioning.md.");
        }

        [Test]
        public void FloatsPerFrame_IsTheSumOfBlocks()
        {
            int expected = RacingObservationLayout.BaseObservationFloats
                         + RacingObservationLayout.RaceContextFloats
                         + RacingObservationLayout.FrontConeFloats;
            Assert.That(expected, Is.EqualTo(RacingObservationLayout.FloatsPerFrame),
                "FloatsPerFrame must equal Base + RaceContext + FrontCone. " +
                "Update both the per-block constant AND FloatsPerFrame, or one " +
                "of the writers will silently disagree with the policy's input shape.");
        }

        [Test]
        public void BlockSizes_MatchGolden()
        {
            Assert.That(RacingObservationLayout.BaseObservationFloats, Is.EqualTo(GoldenBaseObservationFloats));
            Assert.That(RacingObservationLayout.RaceContextFloats,     Is.EqualTo(GoldenRaceContextFloats));
            Assert.That(RacingObservationLayout.FrontConeFloats,       Is.EqualTo(GoldenFrontConeFloats));
        }

        [Test]
        public void WallRayAnglesArray_MatchesDeclaredCount()
        {
            Assert.That(RacingObservationLayout.WallRayAnglesRad.Length,
                Is.EqualTo(RacingObservationLayout.WallRayCount),
                "WallRayAnglesRad array length must match WallRayCount. " +
                "Both are read by the writer and the policy schema; drift here " +
                "either crashes WriteBase (out-of-bounds) or skews the angle " +
                "interpretation silently.");
        }

        [Test]
        public void LookaheadSecondsArray_MatchesDeclaredCount()
        {
            Assert.That(RacingObservationLayout.LookaheadSeconds.Length,
                Is.EqualTo(RacingObservationLayout.LookaheadAnchors),
                "LookaheadSeconds array length must match LookaheadAnchors.");
        }

        [Test]
        public void MaxLookaheadSeconds_MatchesTheLastEntryInTheArray()
        {
            float last = RacingObservationLayout.LookaheadSeconds[
                RacingObservationLayout.LookaheadSeconds.Length - 1];
            Assert.That(RacingObservationLayout.MaxLookaheadSeconds, Is.EqualTo(last),
                "MaxLookaheadSeconds is read by diagnostic / overlay code; it must " +
                "equal the largest entry in LookaheadSeconds or those overlays drift " +
                "from what the policy actually saw.");
        }

        [Test]
        public void OpponentRayAngleRad_SpansTheFullCone()
        {
            float first = RacingObservationLayout.OpponentRayAngleRad(0);
            float last  = RacingObservationLayout.OpponentRayAngleRad(
                RacingObservationLayout.OpponentRayCount - 1);
            Assert.That(first, Is.EqualTo(-RacingObservationLayout.ConeHalfAngleRad).Within(1e-4f),
                "First opponent ray should sit at -ConeHalfAngleRad.");
            Assert.That(last,  Is.EqualTo(+RacingObservationLayout.ConeHalfAngleRad).Within(1e-4f),
                "Last opponent ray should sit at +ConeHalfAngleRad.");
        }

        [Test]
        public void WallRays_AreSymmetricAroundCentre()
        {
            // The wall feelers are listed as -90, -45, -22.5, 0, +22.5, +45, +90.
            // Trained policies see this exact ordering; an off-by-one or sign
            // flip in the array silently swaps left/right wall distances.
            var a = RacingObservationLayout.WallRayAnglesRad;
            int n = a.Length;
            Assert.That(n % 2, Is.EqualTo(1), "Wall ray array should be odd-length for a centred 0° ray.");
            int mid = n / 2;
            Assert.That(a[mid], Is.EqualTo(0f).Within(1e-6f),
                "Middle wall ray must be 0 (straight ahead).");
            for (int i = 0; i < mid; i++)
            {
                Assert.That(a[i], Is.EqualTo(-a[n - 1 - i]).Within(1e-6f),
                    $"Wall ray pair ({i}, {n - 1 - i}) is no longer mirror-symmetric.");
            }
        }

        /// <summary>
        /// Stable hash of (FloatsPerFrame, every block size, every wall-ray
        /// angle, every lookahead second). Phase 5: moved from
        /// <c>string.GetHashCode</c> (non-stable across .NET versions) to
        /// FNV-1a 64-bit hex via <see cref="RacingObservationLayout.ComputeLayoutHash"/>.
        /// The same value is baked into every manifest's
        /// <c>observation.layoutHash</c> field; the
        /// <c>ManifestParityTests.Latest_LayoutHash_Matches_RacingV1</c> test
        /// pairs the two so drift on either side fails CI.
        ///
        /// Determinism check: the hash for the same input must round-trip
        /// identically across runs.
        /// </summary>
        [Test]
        public void LayoutHash_Is_Stable_Across_Calls()
        {
            string a = RacingObservationLayout.ComputeLayoutHash();
            string b = RacingObservationLayout.ComputeLayoutHash();
            Assert.That(b, Is.EqualTo(a), "ComputeLayoutHash must be deterministic.");
            Debug.Log($"[ObsLayoutSnapshot] live ComputeLayoutHash() = {a}. " +
                      "If the layout drifted, paste this into the manifest's observation.layoutHash field " +
                      "(after snapshotting the previous canonical to a new <id>.json).");
        }
    }
}
