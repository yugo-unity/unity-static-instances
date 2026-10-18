using System;
using NUnit.Framework;

namespace StaticInstances.Editor.Tests
{
    // Derived draw limits use bit-exact arithmetic so boundary decisions stay
    // stable for content using the Distance LOD metric.
    public sealed class StaticInstanceDerivedCullLimitTests
    {
        // Reference derived-limit expression `2*r*scale + r`, written out
        // independently of the code under test.
        private static float ReferenceDerivedLimit(float radius, float sizeScale) =>
            radius * 2f * sizeScale + radius;

        private static void AssertBitIdentical(float expected, float actual, string because)
        {
            Assert.AreEqual(
                BitConverter.SingleToInt32Bits(expected),
                BitConverter.SingleToInt32Bits(actual),
                because + $" (expected {expected:R}, got {actual:R})");
        }

        // A spread rather than one value: the identity has to hold across the
        // exponent range, and a float multiply that is exact at 1.0 can still
        // be asked to round somewhere else.
        private static readonly float[] Radii =
            { 0.01f, 0.25f, 1f, 1.5f, 7.3f, 42f, 137.9f, 1000f, 12345.678f };

        private static readonly float[] Scales =
            { 0.001f, 0.1f, 0.5f, 1f, 3f, 12.5f, 50f, 1000f };

        [Test]
        public void AtIdentityShape_TheDistanceLegMatchesTheReferenceExpression()
        {
            foreach (float radius in Radii)
            {
                foreach (float scale in Scales)
                {
                    float actual = StaticInstanceRendererSystem.ResolveDerivedCullLimit(
                        radius, 1f, scale, cullRatio: 0f, screenSizeMode: false);

                    AssertBitIdentical(
                        ReferenceDerivedLimit(radius, scale), actual,
                        $"radius {radius}, scale {scale}: shape factor 1 must " +
                        "reproduce the reference expression bit for bit");
                }
            }
        }

        [Test]
        public void TheShapeFactorMultipliesOnlyTheSizeTerm_NotTheGeometricOffset()
        {
            // The trailing `+ radius` puts the limit on the instance's SURFACE.
            // Scaling it too would pull the limit inside the geometry for thin
            // content, which is a rendering defect rather than a tuning
            // difference — so the split is pinned, not just commented.
            const float radius = 10f;
            const float scale = 2f;
            const float shape = 0.5f;

            float actual = StaticInstanceRendererSystem.ResolveDerivedCullLimit(
                radius, shape, scale, cullRatio: 0f, screenSizeMode: false);

            AssertBitIdentical(
                radius * 2f * scale * shape + radius, actual,
                "only the size term takes the correction");

            // And the wrong composition — correcting the whole expression —
            // must NOT be what ships.
            Assert.AreNotEqual(
                (radius * 2f * scale + radius) * shape, actual,
                "the offset must survive the correction unscaled");
        }

        [Test]
        public void ANonIdentityShape_OnlyEverShrinksTheLimit()
        {
            // A shape factor of at most 1 can only tighten a cull distance.
            foreach (float radius in Radii)
            {
                float identity = StaticInstanceRendererSystem.ResolveDerivedCullLimit(
                    radius, 1f, 4f, 0f, false);
                float corrected = StaticInstanceRendererSystem.ResolveDerivedCullLimit(
                    radius, 0.37f, 4f, 0f, false);

                Assert.Less(
                    corrected, identity,
                    $"radius {radius}: a shape factor below 1 must tighten the limit");
            }
        }

        [Test]
        public void WithTheKnobOff_ThereIsNoDerivedLimitAtAll_InEitherMode()
        {
            // The derived-limit scale defaults to 0. `float.MaxValue` is the "no derived
            // candidate" sentinel the caller compares against the authored Max.
            Assert.AreEqual(
                float.MaxValue,
                StaticInstanceRendererSystem.ResolveDerivedCullLimit(
                    5f, 1f, 0f, 0f, false),
                "scale 0 must leave the authored Max as the only candidate");

            Assert.AreEqual(
                float.MaxValue,
                StaticInstanceRendererSystem.ResolveDerivedCullLimit(
                    5f, 1f, 0f, 0f, true),
                "an unauthored Culled ratio must leave no ratio-derived candidate");
        }

        [Test]
        public void DistanceModeNeverReadsTheCulledRatio()
        {
            // Mode gating keeps a Distance-mode owner out of the ratio arithmetic.
            // A Distance-mode owner carries cullRatio 0 by construction (the caller
            // zeroes it), but the seam must not depend on the caller having done
            // that — the branch is the guarantee.
            float withRatio = StaticInstanceRendererSystem.ResolveDerivedCullLimit(
                12f, 1f, 3f, cullRatio: 0.5f, screenSizeMode: false);
            float withoutRatio = StaticInstanceRendererSystem.ResolveDerivedCullLimit(
                12f, 1f, 3f, cullRatio: 0f, screenSizeMode: false);

            AssertBitIdentical(
                withoutRatio, withRatio,
                "a stray ratio must not reach Distance mode's arithmetic");
        }

        [Test]
        public void ScreenSizeModeNeverReadsTheSizeCullScale()
        {
            // The mirror of the above, and the reason the caller resolves an
            // "effective" scale per owner: the global Size Cull Distance Scale
            // is INERT for a Screen Size owner, because a ratio
            // cutoff already IS size-proportional culling in this mode's unit.
            float withScale = StaticInstanceRendererSystem.ResolveDerivedCullLimit(
                12f, 1f, effectiveSizeScale: 9f, cullRatio: 0.25f, screenSizeMode: true);
            float withoutScale = StaticInstanceRendererSystem.ResolveDerivedCullLimit(
                12f, 1f, effectiveSizeScale: 0f, cullRatio: 0.25f, screenSizeMode: true);

            AssertBitIdentical(
                withoutScale, withScale,
                "the size-cull knob must be inert in Screen Size mode");
        }

        [Test]
        public void TheRatioLegCarriesNoGeometricOffset()
        {
            // `r / ratio`, not `r / ratio + r`: the ratio already describes the
            // sphere's projection, so the offset would be counted twice. Worth
            // pinning because the Distance leg two lines away DOES carry it,
            // and copying that line is the obvious mistake.
            const float radius = 8f;
            const float ratio = 0.25f;

            AssertBitIdentical(
                radius / ratio,
                StaticInstanceRendererSystem.ResolveDerivedCullLimit(
                    radius, 1f, 0f, ratio, true),
                "the ratio leg must not add the surface offset");
        }

        [Test]
        public void TheRatioLegTakesTheShapeCorrectedRadius()
        {
            // The mode asks how much screen an instance
            // covers, which is exactly the quantity the shape factor corrects,
            // so the ratio leg uses the corrected radius like the ladder does.
            const float radius = 8f;
            const float shape = 0.4f;
            const float ratio = 0.25f;

            AssertBitIdentical(
                radius * shape / ratio,
                StaticInstanceRendererSystem.ResolveDerivedCullLimit(
                    radius, shape, 0f, ratio, true),
                "Screen Size measures the corrected size, not the raw sphere");
        }
    }
}
