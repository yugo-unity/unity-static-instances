using System.Reflection;
using NUnit.Framework;

namespace StaticInstances.Editor.Tests
{
    // The count and write halves must apply the SAME expression: a predicate
    // present in one half and absent from the other writes past a cursor or
    // leaves a hole.
    //
    // The implementation makes that structurally true by routing every one of
    // the seven emission sites through ONE static, `ShadowCastEmits`. These
    // tests drive that function over the states the emit code actually produces,
    // and assert the property the count/write pair depends on: the predicate is
    // a pure function of (level, threshold), so the two halves cannot disagree.
    //
    // The rule is direction-agnostic ON PURPOSE. A crossfade pair's `from` may
    // be above OR below its `to` (`ApplyLodLevel` starts a fade on any level
    // change, in either direction); a direction-specific rule would delete an
    // approaching owner for a whole band, then pop it on.
    // `OwnerRefusal_IsDirectionAgnostic` drives the OWNER-level refusal, which is
    // where that rule lives.
    public sealed class StaticInstanceShadowCastMaxLevelTests
    {
        private const byte NoThreshold = 0xFF;

        private static bool Emits(int level, byte threshold)
        {
            MethodInfo method = typeof(StaticInstanceRendererSystem).GetMethod(
                "ShadowCastEmits",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(
                method, Is.Not.Null,
                "StaticInstanceRendererSystem.ShadowCastEmits is gone. It is the " +
                "single statement of the shadow-cast max-level rule; every emission site must call it.");
            return (bool)method.Invoke(null, new object[] { level, threshold });
        }

        [Test]
        public void NoThreshold_EveryLevelCasts()
        {
            // 0xFF is how the scratch region carries the authored -1.
            for (int level = 0; level < 4; level++)
            {
                Assert.That(
                    Emits(level, NoThreshold), Is.True,
                    $"level {level} must cast when the owner authors no threshold — " +
                    "this is the default (-1).");
            }
        }

        [Test]
        public void Threshold_EmitsAtOrBelowAndDropsAbove()
        {
            Assert.That(Emits(0, 0), Is.True, "level 0 is at the threshold 0");
            Assert.That(Emits(1, 0), Is.False, "level 1 is above the threshold 0");
            Assert.That(Emits(1, 1), Is.True, "level 1 is at the threshold 1");
            Assert.That(Emits(2, 1), Is.False, "level 2 is above the threshold 1");
        }

        // The direction rule lives in the OWNER-level refusal, not in the per-copy
        // predicate, so these tests drive `ShadowCastOwnerRefused` directly.
        private static bool OwnerRefused(int from, int to, int k, int n)
        {
            MethodInfo method = typeof(StaticInstanceRendererSystem).GetMethod(
                "ShadowCastOwnerRefused",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            Assert.That(
                method, Is.Not.Null,
                "StaticInstanceRendererSystem.ShadowCastOwnerRefused is gone. It " +
                "is the owner-level half of the shadow-cast max-level rule.");
            return (bool)method.Invoke(null, new object[] { from, to, k, n });
        }

        [Test]
        public void OwnerRefusal_IsDirectionAgnostic()
        {
            const int n = 1;
            const int k = 64; // any live pair

            // Departing (camera receding): from = 1, to = 2. The lower half is
            // at the threshold, so the owner must NOT be refused.
            Assert.That(
                OwnerRefused(from: 1, to: 2, k: k, n: n), Is.False,
                "a departing pair straddling the threshold must reach the emit " +
                "path so its lower half can still cast");

            // Approaching (camera closing): from = 2, to = 1, reachable because
            // ApplyLodLevel starts a fade on any level change.
            Assert.That(
                OwnerRefused(from: 2, to: 1, k: k, n: n), Is.False,
                "an approaching pair must not be refused: its finer half still casts " +
                "during the fade-in");
        }

        [Test]
        public void OwnerRefusal_RefusesOnlyWhenNothingWouldBeEmitted()
        {
            const int n = 1;

            // No pair, past the threshold: refuse.
            Assert.That(OwnerRefused(from: 0, to: 2, k: 0, n: n), Is.True);
            // No pair, at the threshold: emit.
            Assert.That(OwnerRefused(from: 0, to: 1, k: 0, n: n), Is.False);
            // Pair wholly above: refuse.
            Assert.That(OwnerRefused(from: 2, to: 3, k: 64, n: n), Is.True);
            // Pair wholly at or below: emit.
            Assert.That(OwnerRefused(from: 0, to: 1, k: 64, n: n), Is.False);
        }

        [Test]
        public void OwnerRefusal_NeverRefusesWithoutAThreshold()
        {
            for (int to = 0; to < 4; to++)
            {
                Assert.That(
                    OwnerRefused(from: 0, to: to, k: 0, n: -1), Is.False,
                    $"level {to} must not be refused when the owner authors no " +
                    "threshold — this is the default (-1).");
            }
        }

        // `k == 0` means no pair, so `from` must be IGNORED. A stale `from`
        // left over from a completed fade would otherwise keep an owner alive
        // past its threshold.
        [Test]
        public void OwnerRefusal_IgnoresFrom_WhenNoPairIsLive()
        {
            Assert.That(
                OwnerRefused(from: 0, to: 2, k: 0, n: 1), Is.True,
                "with k == 0 the decision is `to` alone; a stale `from` must not " +
                "rescue the owner.");
        }

        // The three rows of the pair table that the PER-COPY predicate can express.
        // The fourth, "no pair", is decided by the owner-level refusal instead and
        // is covered by OwnerRefusal_RefusesOnlyWhenNothingWouldBeEmitted.
        [Test]
        public void PairRows_FollowTheEmitRule()
        {
            const byte n = 1;

            // wholly at or below N: both halves emit.
            Assert.That(Emits(0, n) && Emits(1, n), Is.True);

            // straddles: exactly one half emits.
            Assert.That(Emits(1, n) ^ Emits(2, n), Is.True);

            // wholly above N: neither half emits, so the owner-level refusal
            // in IsOwnerRendered can short-circuit the whole owner.
            Assert.That(Emits(2, n) || Emits(3, n), Is.False);
        }

        // The single-copy path: a draw-range fade inside a straddling pair emits
        // one copy at `to` rather than a pair, so the rule is applied to that
        // one level.
        [Test]
        public void SingleCopyPath_IsTestedOnItsOwnLevel()
        {
            Assert.That(Emits(0, 0), Is.True, "a single copy at level 0 under threshold 0 emits");
            Assert.That(Emits(1, 0), Is.False, "a single copy at level 1 under threshold 0 does not");
        }

        // The forced-level composition. The two policies are NOT symmetric: only
        // PerInstance carries `ShadowCastEmits` on its emission paths; for a
        // PerOwner owner this refusal is the whole implementation, so it must
        // test the RAW forced level the emit path bins by.
        //
        // These assert the SHAPE the code must have rather than re-running its
        // expression: a forced level past the threshold must refuse a PerOwner
        // owner, and must not refuse a PerInstance owner whose window clamps
        // the force back to or below the threshold.
        [Test]
        public void ForcedLevel_PerOwner_RefusesOnTheRawLevel()
        {
            // Window [0, 2], threshold 0, ForcedMeshLod = 2. The fast path
            // would emit at level 2 with no predicate of its own, so the
            // refusal has to fire.
            Assert.That(
                OwnerRefused(from: 0, to: 2, k: 0, n: 0), Is.True,
                "a PerOwner owner forced past its threshold must be refused — " +
                "its emission paths carry no per-copy predicate, so nothing " +
                "downstream would drop it.");
        }

        [Test]
        public void ForcedLevel_PerInstance_ClampedIntoTheWindowDoesNotRefuse()
        {
            // SIS window [0, 1], threshold 1, ForcedMeshLod = 2 → the eval
            // clamps to 1, which is at the threshold, so the owner must reach
            // the emit path and let ShadowCastEmits decide per instance.
            const int clampedIntoWindow = 1;
            Assert.That(
                OwnerRefused(from: 0, to: clampedIntoWindow, k: 0, n: 1), Is.False,
                "a PerInstance owner whose forced level clamps back into its " +
                "window must not be refused — over-refusal kills its shadows " +
                "outright, where under-refusal is caught per copy.");
        }

        [Test]
        public void OwnerDefault_IsMinusOne()
        {
            var owner = new StaticInstanceOwner();
            Assert.That(
                owner.ShadowCastMaxLevel, Is.EqualTo((sbyte)-1),
                "the default must be -1 (every level casts)");
        }
    }
}
