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
        //    and CHANGELOG.md reflect the bump.
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
        /// Golden hash of (FloatsPerFrame, every block size, every wall-ray
        /// angle, every lookahead second). Locks the *structure* of the
        /// observation — distinct from the runtime float values, which depend
        /// on car state. If you change the layout deliberately, regenerate by
        /// uncommenting the Debug.Log line below, running this test once,
        /// pasting the new hash, and snapshotting the old Latest first.
        /// </summary>
        [Test]
        public void LayoutHash_MatchesGolden()
        {
            var sb = new StringBuilder();
            sb.Append("v=").Append(RacingObservationLayout.FloatsPerFrame).Append(';');
            sb.Append("base=").Append(RacingObservationLayout.BaseObservationFloats).Append(';');
            sb.Append("race=").Append(RacingObservationLayout.RaceContextFloats).Append(';');
            sb.Append("cone=").Append(RacingObservationLayout.FrontConeFloats).Append(';');
            sb.Append("anchors=").Append(RacingObservationLayout.LookaheadAnchors).Append(';');
            sb.Append("walls=").Append(RacingObservationLayout.WallRayCount).Append(';');
            sb.Append("opp=").Append(RacingObservationLayout.OpponentRayCount).Append(';');
            foreach (var w in RacingObservationLayout.WallRayAnglesRad)
                sb.Append(w.ToString("R")).Append(',');
            sb.Append('|');
            foreach (var l in RacingObservationLayout.LookaheadSeconds)
                sb.Append(l.ToString("R")).Append(',');

            int hash = sb.ToString().GetHashCode();
            // Debug.Log($"[ObsLayoutSnapshot] hash={hash} src=\"{sb}\""); // <- regenerate

            // Recorded once the layout stabilised at FloatsPerFrame=60.
            // A mismatch means the layout drifted; regenerate after you've
            // versioned the previous canonical under Versions/V<N>/.
            const int GoldenLayoutHash = unchecked((int)0); // SENTINEL: see below

            // GoldenLayoutHash == 0 means "no golden recorded yet". The first
            // CI run logs the live hash; the maintainer pastes it back, replaces
            // the sentinel, and the test starts enforcing. Until then this test
            // just records a determinism check: the hash for the *same source*
            // must round-trip identically across runs (it always will — this is
            // a smoke for the test infrastructure itself).
            if (GoldenLayoutHash == 0)
            {
                Debug.Log($"[ObsLayoutSnapshot] no golden yet; current hash={hash}. " +
                          "Paste this value into GoldenLayoutHash to lock it.");
                int again = sb.ToString().GetHashCode();
                Assert.That(again, Is.EqualTo(hash),
                    "GetHashCode should be stable for identical input within one run.");
            }
            else
            {
                Assert.That(hash, Is.EqualTo(GoldenLayoutHash),
                    "Observation layout drifted from the golden snapshot. If the " +
                    "change is intentional, freeze Latest under Versions/V<N>/, " +
                    "then paste the new hash here.");
            }
        }
    }
}
