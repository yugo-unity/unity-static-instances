using NUnit.Framework;

namespace StaticInstances.Editor.Tests
{
    // The runtime half of the Hysteresis rule: the per-component Hysteresis guards the
    // Draw Range limits as well as the LOD boundaries, wherever no band is fading them.
    //
    // These drive the two rules directly (the job they live in is a Burst
    // IJobParallelFor over every instance, and the arithmetic is the only part
    // of it that can be wrong about semantics). Each case is a camera sitting
    // on a limit, which is the situation the margin exists for — and the pair
    // that matters most is "leaves at the authored limit / returns only at
    // limit − h", because a margin applied in both directions would just be a
    // shifted limit rather than hysteresis.
    public sealed class StaticInstanceDrawRangeHysteresisTests
    {
        private const float Opaque = 127f;
        private const float Out = 0f;

        // -- Distance-driven (SIS), no band: the hard cut is guarded.

        [Test]
        public void AVisibleInstance_LeavesAtTheAuthoredLimit()
        {
            Assert.AreEqual(
                Opaque, Fade(distance: 99.9f, previousFade: Opaque), 0.01f,
                "Just inside the limit it must stay drawn.");
            Assert.AreEqual(
                Out, Fade(distance: 100.1f, previousFade: Opaque), 0.01f,
                "The exit is the AUTHORED limit — hysteresis must not push the " +
                "limit itself outwards, or every owner silently draws further " +
                "than it says.");
        }

        [Test]
        public void AnInstanceThatLeft_ComesBackOnlyInsideTheMargin()
        {
            Assert.AreEqual(
                Out, Fade(distance: 97f, previousFade: Out),
                "Inside the limit but inside the margin too: coming back here " +
                "is what makes a camera resting on the limit flicker.");
            Assert.AreEqual(
                Opaque, Fade(distance: 94f, previousFade: Out), 0.01f,
                "Past the margin it must come back — hysteresis delays the " +
                "return, it does not cancel it.");
        }

        // The property the stateless path exists for. Everything outside the
        // h-wide margin resolves in one frame from any previous state, so a
        // teleport still settles immediately.
        [Test]
        public void OutsideTheMargin_ThePreviousStateDoesNotMatter()
        {
            Assert.AreEqual(Opaque, Fade(distance: 10f, previousFade: Out), 0.01f);
            Assert.AreEqual(Opaque, Fade(distance: 10f, previousFade: Opaque), 0.01f);
            Assert.AreEqual(Out, Fade(distance: 500f, previousFade: Opaque), 0.01f);
            Assert.AreEqual(Out, Fade(distance: 500f, previousFade: Out), 0.01f);
        }

        // Content that authors a fade band is not touched by any of this: the
        // band is what governs, and it is a pure function of distance.
        [Test]
        public void ABandedSide_IsUnchanged_AndStaysStateless()
        {
            float withHistory = StaticInstanceRendererSystem.ResolveDistanceDrawRangeFade(
                distance: 95f, far: 100f, near: 0f,
                farWidth: 10f, nearWidth: 0f, hysteresis: 5f, previousFade: Out);
            float withoutHistory = StaticInstanceRendererSystem.ResolveDistanceDrawRangeFade(
                distance: 95f, far: 100f, near: 0f,
                farWidth: 10f, nearWidth: 0f, hysteresis: 5f, previousFade: Opaque);

            Assert.AreEqual(
                withHistory, withoutHistory, 0.001f,
                "A Distance-mode fade must remain a pure function of distance — " +
                "this is the property that makes stopping inside the band hold a " +
                "stable dither.");
            Assert.AreEqual(
                Opaque * 0.5f, withHistory, 0.01f,
                "halfway through a 10 m band should be half faded");
        }

        // The near limit is the mirror image, and it has its own trap: an
        // owner with no near limit (0) must not get a margin conjured around
        // a limit that does not exist.
        [Test]
        public void TheNearLimitMirrorsIt_AndZeroMeansNoNearBandAtAll()
        {
            Assert.AreEqual(
                Out,
                StaticInstanceRendererSystem.ResolveDistanceDrawRangeFade(
                    distance: 12f, far: 100f, near: 10f,
                    farWidth: 0f, nearWidth: 0f, hysteresis: 5f, previousFade: Out),
                "Inside the near margin from outside: it must stay hidden.");
            Assert.AreEqual(
                Opaque,
                StaticInstanceRendererSystem.ResolveDistanceDrawRangeFade(
                    distance: 16f, far: 100f, near: 10f,
                    farWidth: 0f, nearWidth: 0f, hysteresis: 5f, previousFade: Out),
                0.01f,
                "Past the near margin it appears.");
            Assert.AreEqual(
                Opaque,
                StaticInstanceRendererSystem.ResolveDistanceDrawRangeFade(
                    distance: 0.5f, far: 100f, near: 0f,
                    farWidth: 0f, nearWidth: 0f, hysteresis: 5f, previousFade: Out),
                0.01f,
                "With no near limit authored there is nothing to be inside of — " +
                "an instance at the camera must draw, not be held out by a " +
                "margin around 0.");
        }

        // -- Time-driven: the fade runs on a clock, so the state read
        // has to be "settled", not "not zero".

        [Test]
        public void TimeDriven_AnInstanceMidFadeOut_IsNotPulledBackByJitter()
        {
            // Sitting just inside the limit, part-way through a fade-out: the
            // plain test says "in band", and answering yes here would keep re-triggering the fade.
            Assert.IsFalse(
                InBand(distance: 99f, previousFade: Opaque * 0.5f),
                "A fade-out in progress must not be reversed by an inward " +
                "jitter of less than the margin — that reversal, repeated, is " +
                "the full-cycle pumping the margin prevents.");
            Assert.IsTrue(
                InBand(distance: 94f, previousFade: Opaque * 0.5f),
                "Past the margin the fade-out is reversed, as it should be.");
        }

        [Test]
        public void TimeDriven_ASettledInstance_LeavesAtTheAuthoredLimit()
        {
            Assert.IsTrue(InBand(distance: 99.9f, previousFade: Opaque));
            Assert.IsFalse(InBand(distance: 100.1f, previousFade: Opaque));
        }

        [Test]
        public void TimeDriven_WithNoHysteresis_ItIsExactlyThePlainTest()
        {
            Assert.IsTrue(
                StaticInstanceRendererSystem.ResolveTimeDrawRangeInBand(
                    distanceSq: 99f * 99f, farSq: 100f * 100f, nearSq: 0f,
                    hysteresis: 0f, previousFade: Out),
                "Hysteresis 0 reduces the in-band test to the plain near/far comparison.");
        }

        // An owner with no far limit stores float.MaxValue as its squared
        // threshold. The margin arithmetic must not turn that into a
        // comparison that rejects everything.
        [Test]
        public void TimeDriven_AnUnlimitedFarThreshold_StillDraws()
        {
            Assert.IsTrue(
                StaticInstanceRendererSystem.ResolveTimeDrawRangeInBand(
                    distanceSq: 1e6f, farSq: float.MaxValue, nearSq: 0f,
                    hysteresis: 5f, previousFade: Out));
        }

        // -- The margin may never exceed half the band.

        // With the default 5 m margin and a Max of 5 m or less, the far re-entry
        // threshold would land at or below 0, so an instance that crossed out once
        // would never come back: boundary flicker traded for permanent disappearance.
        [Test]
        public void AMarginWiderThanTheBand_IsClampedSoTheContentCanReturn()
        {
            float returned = StaticInstanceRendererSystem.ResolveDistanceDrawRangeFade(
                distance: 2f, far: 5f, near: 0f,
                farWidth: 0f, nearWidth: 0f, hysteresis: 5f, previousFade: Out);

            Assert.AreEqual(
                Opaque, returned, 0.01f,
                "With Max 5 and Hysteresis 5 the unclamped re-entry threshold is " +
                "0 m, so nothing could ever come back. Clamped to half the band " +
                "it returns at 2.5 m.");
            Assert.AreEqual(
                Out,
                StaticInstanceRendererSystem.ResolveDistanceDrawRangeFade(
                    distance: 4f, far: 5f, near: 0f,
                    farWidth: 0f, nearWidth: 0f, hysteresis: 5f, previousFade: Out),
                "The clamp must not remove the margin, only bound it — 4 m is " +
                "still inside it.");
        }

        // The pair the Inspector's 0.1 m push separation makes authorable, and
        // the reason the bound is HALF the band: at anything wider the two
        // re-entry thresholds cross and the guarded region has no interior.
        [Test]
        public void ANarrowBand_KeepsANonEmptyInterior()
        {
            Assert.AreEqual(
                Opaque,
                StaticInstanceRendererSystem.ResolveDistanceDrawRangeFade(
                    distance: 50.05f, far: 50.1f, near: 50f,
                    farWidth: 0f, nearWidth: 0f, hysteresis: 5f, previousFade: Out),
                0.01f,
                "Min 50 / Max 50.1 with Hysteresis 5: the margins meet in the " +
                "middle rather than crossing, so the band still has a distance " +
                "at which content re-enters.");
        }

        [Test]
        public void TheClampIsInert_WhereTheBandIsComfortablyWider()
        {
            Assert.AreEqual(
                5f,
                StaticInstanceRendererSystem.EffectiveHysteresis(far: 100f, near: 0f, hysteresis: 5f),
                0.001f,
                "Ordinary content must be completely unaffected by the clamp.");
            Assert.AreEqual(
                5f,
                StaticInstanceRendererSystem.EffectiveHysteresis(
                    far: float.MaxValue, near: 0f, hysteresis: 5f),
                0.001f,
                "An owner with no far limit has an unbounded band; the clamp " +
                "must not collapse on the sentinel.");
            Assert.AreEqual(
                2.5f,
                StaticInstanceRendererSystem.EffectiveHysteresis(far: 5f, near: 0f, hysteresis: 5f),
                0.001f);
        }

        // Stated as the invariant rather than as three examples, because it is
        // what makes the bound the right one rather than a tuned one.
        [Test]
        public void TheTwoReEntryThresholds_NeverCross()
        {
            float[] limits = { 0.5f, 5f, 50f, 1000f };
            float[] nears = { 0f, 1f, 49f };
            float[] margins = { 0f, 0.1f, 5f, 500f };
            foreach (float far in limits)
            {
                foreach (float near in nears)
                {
                    if (near >= far)
                        continue;
                    foreach (float h in margins)
                    {
                        float margin = StaticInstanceRendererSystem.EffectiveHysteresis(far, near, h);
                        Assert.LessOrEqual(
                            near + margin, far - margin + 1e-4f,
                            $"far={far} near={near} h={h}: the re-entry thresholds " +
                            "crossed, which leaves a band nothing can re-enter.");
                    }
                }
            }
        }

        [Test]
        public void TimeDriven_TheSameClampApplies()
        {
            Assert.IsTrue(
                StaticInstanceRendererSystem.ResolveTimeDrawRangeInBand(
                    distanceSq: 2f * 2f, farSq: 5f * 5f, nearSq: 0f,
                    hysteresis: 5f, previousFade: Out),
                "The time path applies the same half-band clamp, so with Max 5 and " +
                "Hysteresis 5 an instance re-enters at 2.5 m.");
            Assert.IsFalse(
                StaticInstanceRendererSystem.ResolveTimeDrawRangeInBand(
                    distanceSq: 4f * 4f, farSq: 5f * 5f, nearSq: 0f,
                    hysteresis: 5f, previousFade: Out),
                "and it keeps the margin it is allowed");
        }

        private static float Fade(float distance, float previousFade) =>
            StaticInstanceRendererSystem.ResolveDistanceDrawRangeFade(
                distance, far: 100f, near: 0f,
                farWidth: 0f, nearWidth: 0f, hysteresis: 5f, previousFade: previousFade);

        private static bool InBand(float distance, float previousFade) =>
            StaticInstanceRendererSystem.ResolveTimeDrawRangeInBand(
                distance * distance, farSq: 100f * 100f, nearSq: 0f,
                hysteresis: 5f, previousFade: previousFade);
    }
}
