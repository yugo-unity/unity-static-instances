using NUnit.Framework;
using UnityEngine;

namespace StaticInstances.Editor.Tests
{
    // Screen Size evaluation. These pin the RATIO → DISTANCE identity the whole
    // mode rests on:
    //
    //     d_j = r_i / (ratio_j · tan(fov/2))
    //
    // split for evaluation as `(1 / ratio_j) × r_i × (1 / tan(fov/2))` — a
    // per-owner reciprocal, a per-instance radius and the per-camera term.
    // If any of the three drifts the mode silently selects the wrong level: no
    // exception, no log, just content at the wrong detail.
    public sealed class StaticInstanceScreenSizeLadderTests
    {
        private Camera camera;

        [SetUp]
        public void SetUp()
        {
            var go = new GameObject("screen size cam");
            camera = go.AddComponent<Camera>();
            camera.orthographic = false;
        }

        [TearDown]
        public void TearDown()
        {
            if (camera != null)
                Object.DestroyImmediate(camera.gameObject);
        }

        // The closed form, kept as the oracle rather than as a comment.
        private static float ExpectedDistance(float radius, float ratio, float fovDegrees) =>
            radius / (ratio * Mathf.Tan(fovDegrees * 0.5f * Mathf.Deg2Rad));

        [Test]
        public void TheThreeTerms_MultiplyBackToTheClosedForm()
        {
            foreach (float ratio in new[] { 1f, 0.5f, 0.25f, 0.05f })
            {
                foreach (float radius in new[] { 0.25f, 1f, 12f })
                {
                    foreach (float fov in new[] { 20f, 50f, 90f })
                    {
                        camera.fieldOfView = fov;

                        float perOwner = StaticInstanceLodSystem.RatioToBoundary(ratio);
                        float perCamera = StaticInstanceLodSystem.ResolveCameraFovTerm(
                            camera, out bool hasFov);
                        Assert.IsTrue(hasFov);

                        float composed = perOwner * radius * perCamera;
                        float expected = ExpectedDistance(radius, ratio, fov);

                        Assert.AreEqual(
                            1f, composed / expected, 1e-5f,
                            $"ratio {ratio}, radius {radius}, fov {fov}");
                    }
                }
            }
        }

        [Test]
        public void ARatioOfZero_IsNeverCoarser_NotAnInfiniteDistance()
        {
            // 0 means "never coarser than this level". It must reach the
            // ladder as the SAME sentinel a short ladder uses, because the
            // value is about to be MULTIPLIED by a radius — an infinity there
            // would produce NaN for a zero-radius instance.
            Assert.AreEqual(float.MaxValue, StaticInstanceLodSystem.RatioToBoundary(0f));
            Assert.AreEqual(float.MaxValue, StaticInstanceLodSystem.RatioToBoundary(-0.5f));
            Assert.AreEqual(float.MaxValue, StaticInstanceLodSystem.RatioToBoundary(float.NaN));
        }

        [Test]
        public void ASmallerRatio_IsAFartherBoundary_WhichIsTheAxisDirection()
        {
            // THE INVERSION: ratios DESCEND as distance grows. If this ever reads
            // the other way, every authoring guard built on the ratio ladder is
            // backwards.
            float near = StaticInstanceLodSystem.RatioToBoundary(1f);
            float mid = StaticInstanceLodSystem.RatioToBoundary(0.5f);
            float far = StaticInstanceLodSystem.RatioToBoundary(0.25f);

            Assert.Less(near, mid, "ratio 1.0 must be the NEAREST boundary");
            Assert.Less(mid, far, "a smaller ratio must be a farther boundary");
        }

        [Test]
        public void ABiggerInstance_HoldsItsLevelFarther_AtTheSameRatio()
        {
            // The per-instance half of the mode: two instances under one
            // authored ladder get different distances.
            camera.fieldOfView = 50f;
            float term = StaticInstanceLodSystem.ResolveCameraFovTerm(camera, out _);
            float boundary = StaticInstanceLodSystem.RatioToBoundary(0.5f);

            float small = boundary * 0.5f * term;
            float large = boundary * 5f * term;

            Assert.Less(
                small, large,
                "a larger instance must keep its level to a greater distance");
            Assert.AreEqual(
                10f, large / small, 1e-4f,
                "the distance must scale LINEARLY with the radius");
        }

        [Test]
        public void TheNoFovFallback_EqualsACameraSittingAtTheReference()
        {
            // THE NO-FOV FALLBACK, stated as an identity rather than as a
            // branch: with no usable FOV a Screen Size owner is evaluated as
            // if seen at its OWN reference. The job spells that `1f / RefTan`,
            // so it must equal the camera term of a camera whose FOV *is* the
            // reference — otherwise "as if viewed at the reference" is not
            // what the code does.
            foreach (float reference in new[] { 30f, 50f, 73f, 110f })
            {
                float refTan = Mathf.Tan(reference * 0.5f * Mathf.Deg2Rad);
                float fallbackTerm = 1f / refTan;

                camera.fieldOfView = reference;
                float atReference = StaticInstanceLodSystem.ResolveCameraFovTerm(
                    camera, out bool hasFov);

                Assert.IsTrue(hasFov);
                Assert.AreEqual(
                    1f, fallbackTerm / atReference, 1e-5f,
                    $"the no-FOV fallback must match a camera at {reference} degrees");
            }
        }

        [Test]
        public void AnOrthographicView_ReportsNoFov_SoTheFallbackIsWhatRuns()
        {
            // Ortho is out of scope for the FACTOR but a Screen
            // Size owner still has to be drawn at some detail level: the
            // fallback is the intended answer, and this is the flag that selects
            // it.
            camera.orthographic = true;
            StaticInstanceLodSystem.ResolveCameraFovTerm(camera, out bool hasFov);
            Assert.IsFalse(hasFov, "an orthographic view must report no usable FOV");
        }

        [Test]
        public void ZoomingIn_PushesRatioBoundariesOut_LikeTheDistanceModeFactor()
        {
            // Screen Size mode is inherently FOV-tracking: a
            // narrowed FOV makes an instance cover more screen, so it holds a
            // finer level to a greater distance. Same direction as the
            // distance-mode factor, by construction rather than by a shared switch.
            float boundary = StaticInstanceLodSystem.RatioToBoundary(0.5f);

            camera.fieldOfView = 60f;
            float wide = boundary * StaticInstanceLodSystem.ResolveCameraFovTerm(camera, out _);
            camera.fieldOfView = 20f;
            float zoomed = boundary * StaticInstanceLodSystem.ResolveCameraFovTerm(camera, out _);

            Assert.Greater(
                zoomed, wide,
                "zooming in must push a ratio boundary FARTHER out");
        }
    }
}
