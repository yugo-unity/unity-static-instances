using NUnit.Framework;

namespace StaticInstances.Editor.Tests
{
    // The per-instance time-fade state machine, driven frame by frame.
    //
    // `StepTimeFade` is the state machine the Burst job runs; it is driven
    // directly here, frame by frame.
    public sealed class StaticInstanceTimeFadeStateTests
    {
        private const byte FadeOpaque = StaticInstanceRendererSystem.FadeOpaque;
        // Bit 7 set, level 0 in bits 0-1: "evaluated once, settled at 0".
        private const byte Settled0 = 0x80;

        // A transition must emit a pair on its very first frame. k is the incoming
        // level's share, so it is 0 on the transition frame; guarding on `k >= 1`
        // would clear the state instead and make the machine a fixed point at
        // "no fade".
        [Test]
        public void ATransition_EmitsAPairOnTheVeryFirstFrame()
        {
            byte state = Settled0;
            float progress = 0f;

            byte k = StaticInstanceRendererSystem.StepTimeFade(
                1, 0, false, 12.7f, ref state, ref progress, out int partner);

            Assert.AreNotEqual(
                0, k,
                "The fade must be visible on the frame it starts; k = 0 here would mean no fade.");
            Assert.AreEqual(0, partner, "The pair's other half is the level held last frame.");
            Assert.AreEqual(FadeOpaque, progress, 0.001f, "A transition arms the full duration.");
        }

        // k = 0 and k = 127 may never be emitted: the emit sites write k and
        // (256 - k) & 0xFF, so k = 0 gives BOTH halves zero coverage.
        [Test]
        public void TheEmittedK_StaysInsideTheDrawableRange()
        {
            byte state = Settled0;
            float progress = 0f;
            int frames = 0;

            byte k = StaticInstanceRendererSystem.StepTimeFade(
                1, 0, false, 12.7f, ref state, ref progress, out _);
            while (k != 0 && frames++ < 100)
            {
                Assert.GreaterOrEqual(k, 1, "k = 0 draws a hole, not a blend.");
                Assert.LessOrEqual(k, FadeOpaque - 1, "k = 127 is the settled level, not a pair.");
                k = StaticInstanceRendererSystem.StepTimeFade(
                    1, 1, false, 12.7f, ref state, ref progress, out _);
            }

            Assert.Less(frames, 100, "The fade must terminate.");
        }

        // A fade that starts runs to completion — the property the whole feature
        // exists for (a distance band snaps when a zoom crosses it in one
        // frame; this must not).
        [Test]
        public void AFade_RunsToCompletion_AndThenStopsEmitting()
        {
            byte state = Settled0;
            float progress = 0f;
            float step = FadeOpaque / 10f;

            byte k = StaticInstanceRendererSystem.StepTimeFade(
                1, 0, false, step, ref state, ref progress, out _);
            int previous = k;
            int frames = 1;
            while (k != 0)
            {
                Assert.LessOrEqual(previous, k, "The incoming level's share only rises.");
                previous = k;
                k = StaticInstanceRendererSystem.StepTimeFade(
                    1, 1, false, step, ref state, ref progress, out _);
                frames++;
            }

            Assert.AreEqual(0f, progress, 0.001f, "A finished fade leaves no progress behind.");
            Assert.AreEqual(
                1 | 0x80, state, "It settles AT the level it faded to, still marked seen.");
        }

        // Every slice is cleared at rebuild, so the first evaluated
        // frame reads held = 0. Without the settle mark a scene whose
        // instances are really at level 2 would fade every one of them in.
        [Test]
        public void TheFirstEvaluatedFrame_Snaps_RatherThanFadingTheSceneIn()
        {
            byte state = 0;      // cleared by the rebuild: never evaluated
            float progress = 0f;

            byte k = StaticInstanceRendererSystem.StepTimeFade(
                2, 0, false, 12.7f, ref state, ref progress, out _);

            Assert.AreEqual(0, k, "Nothing may fade on the frame the arrays were born.");
            Assert.AreEqual(2 | 0x80, state, "It settles at the true level and marks itself seen.");

            byte second = StaticInstanceRendererSystem.StepTimeFade(
                1, 2, false, 12.7f, ref state, ref progress, out int partner);
            Assert.AreNotEqual(0, second, "The guard must not survive its first frame.");
            Assert.AreEqual(2, partner, "It fades from the level it settled at.");
        }

        // Non-adjacent levels are the reason the partner is carried in bits
        // 2-3 at all: a zoom can move the selection two boundaries in a frame.
        [Test]
        public void ANonAdjacentJump_FadesRatherThanSnapping()
        {
            byte state = Settled0;
            float progress = 0f;

            byte k = StaticInstanceRendererSystem.StepTimeFade(
                3, 0, false, 12.7f, ref state, ref progress, out int partner);

            Assert.AreNotEqual(0, k, "0 -> 3 is a fade like any other.");
            Assert.AreEqual(0, partner, "The pair is (3, 0), not (3, 2).");
        }

        // Snap covers a forced level and the URP Asset's Cross Fade switch. It
        // CLEARS the progress: a fade resumed later from a pose the camera has
        // left would be wrong.
        [Test]
        public void Snap_ClearsAFadeInFlight_RatherThanFreezingIt()
        {
            byte state = Settled0;
            float progress = 0f;
            StaticInstanceRendererSystem.StepTimeFade(
                1, 0, false, 12.7f, ref state, ref progress, out _);
            Assert.Greater(progress, 0f, "fixture: a fade is running.");

            byte k = StaticInstanceRendererSystem.StepTimeFade(
                1, 1, true, 12.7f, ref state, ref progress, out _);

            Assert.AreEqual(0, k, "No pair may be emitted while snapping.");
            Assert.AreEqual(0f, progress, 0.001f, "The fade is dropped, not paused.");
        }

        // What the shadow merge's "finest level across cameras" test must read.
        // For a distance pair bits 0-1 hold min(level, partner), but a TIME pair
        // puts the INCOMING level there: a camera fading 0 -> 1 presents 1 while
        // still drawing 126/127 of level 0, so both halves of a pair must be read.
        [Test]
        public void TheFinestLevel_ReadsBothHalvesOfAPair_ButOnlyWhenThereIsOne()
        {
            // Settled: bits 2-3 are ZERO, and an unguarded min() would call
            // every settled instance level 0. This is the case that makes the
            // pair-bit guard load-bearing rather than decorative.
            Assert.AreEqual(
                2, StaticInstanceRendererSystem.FinestLevel(0x02),
                "A settled level 2 is level 2, not the empty partner field.");

            // Distance pair (level 1, partner 2): the finer half is in bits 0-1.
            Assert.AreEqual(
                1, StaticInstanceRendererSystem.FinestLevel((byte)(1 | (2 << 2) | 0x80)),
                "The distance path already put the finer half in bits 0-1.");

            // A time pair fading 0 -> 1 carries the incoming level in bits 0-1
            // and the finer, still-dominant level in 2-3.
            Assert.AreEqual(
                0, StaticInstanceRendererSystem.FinestLevel((byte)(1 | (0 << 2) | 0x80)),
                "A time fade's outgoing half can be the finer one, and the " +
                "shadow must be judged on what is actually drawn.");

            // Non-adjacent, incoming finer: the other direction of the same
            // rule (3 -> 0 presents 0 with partner 3).
            Assert.AreEqual(
                0, StaticInstanceRendererSystem.FinestLevel((byte)(0 | (3 << 2) | 0x80)),
                "Whichever half is finer wins, in either direction.");
        }

        // A retarget mid-fade restarts from the level held last frame, so there is
        // one pair per instance, never a chain.
        [Test]
        public void ARetargetMidFade_RestartsFromTheHeldLevel()
        {
            byte state = Settled0;
            float progress = 0f;
            StaticInstanceRendererSystem.StepTimeFade(
                1, 0, false, 12.7f, ref state, ref progress, out _);
            StaticInstanceRendererSystem.StepTimeFade(
                1, 1, false, 12.7f, ref state, ref progress, out _);

            StaticInstanceRendererSystem.StepTimeFade(
                2, 1, false, 12.7f, ref state, ref progress, out int partner);

            Assert.AreEqual(1, partner, "The new fade's outgoing half is the level held last frame.");
            Assert.AreEqual(FadeOpaque, progress, 0.001f, "A retarget arms the full duration again.");
        }
    }
}
