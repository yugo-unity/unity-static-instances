using NUnit.Framework;

namespace StaticInstances.Editor.Tests
{
    // The Screen Size absolute-bound seams. These pin the arithmetic of the
    // absolute-bound semantics, which otherwise lives only inside the eval job.
    //
    // Only the semantics can be wrong here, and nothing would say so: a flipped
    // comparison gives the wrong fade width on the wrong leg, with no exception,
    // log or counter. These tests fix the arithmetic in place.
    public sealed class StaticInstanceScreenSizeBoundTests
    {
        [Test]
        public void TheAbsoluteBoundWins_AndBecomesTheAuthoredWinner()
        {
            // The semantics: when the absolute bound is the effective
            // limit it is also the WINNER, so the fade-width rule must see an
            // AUTHORED leg — which is what clearing derivedLimit says.
            bool derivedLimit = true;
            float far = StaticInstanceRendererSystem.ResolveScreenSizeFarBound(
                200f, 120f, ref derivedLimit);

            Assert.AreEqual(120f, far, 1e-4f, "the tighter absolute bound must win");
            Assert.IsFalse(
                derivedLimit,
                "an absolute bound that wins must carry the AUTHORED width, not " +
                "the ratio leg's fraction-of-limit band");
        }

        [Test]
        public void TheRatioLegKeepsItsWidth_WhenItIsTheTighterLimit()
        {
            bool derivedLimit = true;
            float far = StaticInstanceRendererSystem.ResolveScreenSizeFarBound(
                80f, 120f, ref derivedLimit);

            Assert.AreEqual(80f, far, 1e-4f, "the ratio leg was tighter and must stand");
            Assert.IsTrue(
                derivedLimit,
                "the derived leg must keep its fraction-of-limit width");
        }

        [Test]
        public void AnUnauthoredBound_ChangesNothing_IncludingTheWinner()
        {
            // 0 is the expressible "unauthored". It must not read as a cutoff
            // at zero, and it must not steal the winner either.
            bool derivedLimit = true;
            float far = StaticInstanceRendererSystem.ResolveScreenSizeFarBound(
                150f, 0f, ref derivedLimit);

            Assert.AreEqual(150f, far, 1e-4f);
            Assert.IsTrue(derivedLimit, "an unauthored bound must not become the winner");
        }

        [Test]
        public void AtAnExactTie_TheCompositionsOwnWinnerStands()
        {
            // `<` and not `<=`, deliberately: a tie goes to the incumbent, the
            // same rule the rebuild's composition uses. A flip here would be
            // silent, so it is stated as a case.
            bool derivedLimit = true;
            float far = StaticInstanceRendererSystem.ResolveScreenSizeFarBound(
                100f, 100f, ref derivedLimit);

            Assert.AreEqual(100f, far, 1e-4f);
            Assert.IsTrue(
                derivedLimit,
                "at a tie the derived leg stays the winner (tie goes to the incumbent)");
        }

        [Test]
        public void TheNearBound_IsAFloor_SoTheLargerValueWins()
        {
            Assert.AreEqual(
                30f, StaticInstanceRendererSystem.ResolveScreenSizeNearBound(10f, 30f), 1e-4f,
                "an authored near bound must push the floor OUT, not in");
            Assert.AreEqual(
                40f, StaticInstanceRendererSystem.ResolveScreenSizeNearBound(40f, 30f), 1e-4f,
                "a tighter stored near must stand");
            Assert.AreEqual(
                15f, StaticInstanceRendererSystem.ResolveScreenSizeNearBound(15f, 0f), 1e-4f,
                "an unauthored near bound must change nothing");
        }

        [Test]
        public void TheTimePath_ClampsBothSides_InSquares()
        {
            // The time-fade path must honor an authored Draw Range.
            float farSq = 200f * 200f;
            float nearSq = 5f * 5f;

            StaticInstanceRendererSystem.ResolveScreenSizeTimeBounds(
                ref farSq, ref nearSq, 120f, 30f);

            Assert.AreEqual(120f * 120f, farSq, 1f, "the far side must clamp IN");
            Assert.AreEqual(30f * 30f, nearSq, 1f, "the near side must clamp OUT");
        }

        [Test]
        public void TheTimePath_LeavesTighterStoredValuesAlone()
        {
            float farSq = 90f * 90f;
            float nearSq = 40f * 40f;

            StaticInstanceRendererSystem.ResolveScreenSizeTimeBounds(
                ref farSq, ref nearSq, 120f, 30f);

            Assert.AreEqual(90f * 90f, farSq, 1f);
            Assert.AreEqual(40f * 40f, nearSq, 1f);
        }

        [Test]
        public void TheTimePath_IgnoresUnauthoredBounds()
        {
            float farSq = 77f * 77f;
            float nearSq = 3f * 3f;

            StaticInstanceRendererSystem.ResolveScreenSizeTimeBounds(
                ref farSq, ref nearSq, 0f, 0f);

            Assert.AreEqual(77f * 77f, farSq, 1e-2f);
            Assert.AreEqual(3f * 3f, nearSq, 1e-2f);
        }

        [Test]
        public void TheTwoPaths_AgreeOnWhichBoundApplies()
        {
            // The distance path compares distances, the time path squares. They
            // must select the same limit, or a component's draw range would
            // depend on its Fade Mode.
            bool derivedLimit = true;
            float distanceFar = StaticInstanceRendererSystem.ResolveScreenSizeFarBound(
                200f, 120f, ref derivedLimit);

            float farSq = 200f * 200f;
            float nearSq = 0f;
            StaticInstanceRendererSystem.ResolveScreenSizeTimeBounds(
                ref farSq, ref nearSq, 120f, 0f);

            Assert.AreEqual(
                distanceFar * distanceFar, farSq, 1f,
                "the two paths must resolve to the same far limit");
        }
    }
}
