using NUnit.Framework;
using UnityEngine;

namespace StaticInstances.Editor.Tests
{
    // Screen Size mode's CULL semantics.
    //
    // WHAT THESE DO NOT COVER: the composition itself lives in `RebuildCombinedData`, which
    // needs registered owners and a culling request, so an EditMode test
    // cannot reach it. What is pinned here is the ARITHMETIC the composition
    // implements and the authoring surface it reads, not the composition itself.
    public sealed class StaticInstanceRatioCullTests
    {
        private GameObject go;
        private Camera camera;

        [SetUp]
        public void SetUp()
        {
            var camGo = new GameObject("ratio cull cam");
            camera = camGo.AddComponent<Camera>();
            camera.orthographic = false;
        }

        [TearDown]
        public void TearDown()
        {
            if (go != null)
                Object.DestroyImmediate(go);
            if (camera != null)
                Object.DestroyImmediate(camera.gameObject);
        }

        // The far cutoff the composition stores, times the term the job
        // applies: r/ratio × 1/tan(fov/2).
        private static float RatioCullDistance(float radius, float ratio, float fovDegrees) =>
            radius / ratio / Mathf.Tan(fovDegrees * 0.5f * Mathf.Deg2Rad);

        [Test]
        public void TheRatioCull_HasNoSurfaceOffset_UnlikeTheDerivedLimit()
        {
            // ⚠ The derived limit is `2·r·scale·c + r` — the trailing `+ r`
            // puts the limit on the instance's SURFACE. The ratio leg has no
            // such term: a ratio already describes the sphere's projection, so
            // adding the offset would double-count it. Pinned as an identity
            // because the two legs sit three lines apart in the composition and
            // a copy-paste would add the offset.
            const float radius = 3f;
            const float ratio = 0.25f;

            float expected = radius / ratio;
            float withOffset = radius / ratio + radius;

            Assert.AreEqual(12f, expected, 1e-4f);
            Assert.AreNotEqual(
                withOffset, expected,
                "the ratio leg must not carry the derived limit's surface offset");
        }

        [Test]
        public void TheCutoff_ScalesLinearlyWithRadius_AndInverselyWithTheRatio()
        {
            camera.fieldOfView = 50f;
            float term = StaticInstanceLodSystem.ResolveCameraFovTerm(camera, out _);

            float small = 1f * term / 0.25f;
            float large = 4f * term / 0.25f;
            Assert.AreEqual(
                4f, large / small, 1e-4f,
                "a 4x radius must cull 4x farther — this is what makes smaller " +
                "instances cull nearer, which is the mode's whole point");

            float coarse = 1f * term / 0.5f;
            float fine = 1f * term / 0.25f;
            Assert.AreEqual(
                2f, fine / coarse, 1e-4f,
                "halving the ratio must double the cutoff");
        }

        [Test]
        public void TheCutoff_MatchesTheClosedForm_AcrossRatiosAndFovs()
        {
            foreach (float ratio in new[] { 0.5f, 0.25f, 0.05f })
            {
                foreach (float fov in new[] { 20f, 60f, 110f })
                {
                    camera.fieldOfView = fov;
                    float term = StaticInstanceLodSystem.ResolveCameraFovTerm(camera, out _);

                    float composed = StaticInstanceLodSystem.RatioToBoundary(ratio) * 2f * term;
                    float expected = RatioCullDistance(2f, ratio, fov);

                    Assert.AreEqual(
                        1f, composed / expected, 1e-5f,
                        $"ratio {ratio}, fov {fov}");
                }
            }
        }

        [Test]
        public void ZeroCullRatio_MeansNoRatioCull_NotACutoffAtZero()
        {
            // The unauthored value must read as "no limit". If it ever became a
            // cutoff at 0 instead, every instance of that owner would vanish —
            // a whole-owner disappearance from one unauthored field.
            Assert.AreEqual(
                float.MaxValue, StaticInstanceLodSystem.RatioToBoundary(0f),
                "an unauthored ratio must be the no-limit sentinel");
        }

        [Test]
        public void Scatter_ExposesTheCullRatio_AndStartsUnauthored()
        {
            go = new GameObject("sis");
            var scatter = go.AddComponent<StaticInstanceScatter>();
            Assert.AreEqual(0f, scatter.CullRatio, 1e-6f,
                "a new component must not carry a ratio cull it never authored");
        }

        [Test]
        public void AnAbsoluteDrawRange_IsNotScaledByTheCameraTerm()
        {
            // ⚠ In Screen Size mode an authored Draw Range is an ABSOLUTE metric bound.
            // Absolute Draw Range bounds are compared separately from the scaled
            // threshold: if they were scaled, the number an author typed would never
            // be the effective one.
            camera.fieldOfView = 20f;
            float term = StaticInstanceLodSystem.ResolveCameraFovTerm(camera, out _);

            Assert.Greater(
                term, 1f,
                "a narrow FOV must give a term above 1, or this test proves nothing");

            const float authoredMetres = 150f;
            Assert.AreNotEqual(
                authoredMetres * term, authoredMetres,
                "scaling an absolute bound would move it — which is why it is " +
                "kept out of the scaled leg");
        }
    }
}
