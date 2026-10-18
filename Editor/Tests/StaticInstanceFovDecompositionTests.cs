using NUnit.Framework;
using UnityEngine;

namespace StaticInstances.Editor.Tests
{
    // The DECOMPOSITION of the FOV factor.
    //
    // The reference FOV is per component, so the factor is per owner x per camera,
    // while owner params are rebuild-time and shared by every camera slice. The
    // factor is therefore split:
    //
    //     factor = tan(ref/2) × (1 / tan(fov/2))
    //              └ per owner ┘  └── per camera ──┘
    //
    // WHAT THESE TESTS PROTECT: the split is only safe while the two
    // halves multiply back to the SAME number the one-piece formula gives, and
    // while the eval job and the cluster classifier compose them IDENTICALLY.
    // If they diverge, the classifier can call a cluster "uniform" where
    // the per-instance path disagrees: a false uniform, which that function's
    // own comment calls a visual bug. That is a silent failure with no
    // exception and no log, so it is pinned here.
    public sealed class StaticInstanceFovDecompositionTests
    {
        private Camera camera;
        private bool savedTracking;

        [SetUp]
        public void SetUp()
        {
            savedTracking = StaticInstanceLodSystem.FovTrackingEnabled;
            var go = new GameObject("decomposition cam");
            camera = go.AddComponent<Camera>();
            camera.orthographic = false;
        }

        [TearDown]
        public void TearDown()
        {
            StaticInstanceLodSystem.FovTrackingEnabled = savedTracking;
            if (camera != null)
                Object.DestroyImmediate(camera.gameObject);
        }

        // The one-piece formula, kept here as the oracle rather
        // than as a comment: tan(ref/2) / tan(fov/2).
        private static float OnePiece(float referenceDegrees, float fovDegrees) =>
            Mathf.Tan(referenceDegrees * 0.5f * Mathf.Deg2Rad) /
            Mathf.Tan(fovDegrees * 0.5f * Mathf.Deg2Rad);

        [Test]
        public void TheTwoHalves_MultiplyBackToTheOnePieceFormula()
        {
            StaticInstanceLodSystem.FovTrackingEnabled = true;

            foreach (float reference in new[] { 20f, 50f, 60f, 90f, 120f })
            {
                foreach (float fov in new[] { 10f, 20f, 45f, 60f, 100f, 150f })
                {
                    camera.fieldOfView = fov;

                    float cameraTerm = StaticInstanceLodSystem.ResolveCameraFovTerm(
                        camera, out bool hasFov);
                    Assert.IsTrue(hasFov, $"fov {fov} should be usable");

                    float composed =
                        Mathf.Tan(reference * 0.5f * Mathf.Deg2Rad) * cameraTerm;

                    // Relative tolerance: the factor spans ~0.1 to ~10 across
                    // this table, so a fixed epsilon would be meaningless at
                    // one end of it.
                    float expected = OnePiece(reference, fov);
                    Assert.AreEqual(
                        1f, composed / expected, 1e-5f,
                        $"the decomposition drifted at reference {reference}, fov {fov}");
                }
            }
        }

        [Test]
        public void TheScalarResolver_AgreesWithTheComposedHalves()
        {
            // This is the agreement the eval job and the classifier depend on:
            // both compose `RefTan × CameraFovTerm` per owner, while the
            // diagnostics/scalar path resolves the whole factor. If the two
            // ever answer differently, a false uniform becomes possible.
            StaticInstanceLodSystem.FovTrackingEnabled = true;

            foreach (float reference in new[] { 25f, 50f, 75f, 110f })
            {
                foreach (float fov in new[] { 15f, 50f, 95f })
                {
                    camera.fieldOfView = fov;

                    float scalar = StaticInstanceLodSystem.ResolveFovFactor(camera, reference);
                    float cameraTerm = StaticInstanceLodSystem.ResolveCameraFovTerm(
                        camera, out _);
                    float composed =
                        Mathf.Tan(reference * 0.5f * Mathf.Deg2Rad) * cameraTerm;

                    Assert.AreEqual(
                        1f, composed / scalar, 1e-5f,
                        $"scalar and composed disagree at reference {reference}, fov {fov}");
                }
            }
        }

        [Test]
        public void AtItsOwnReference_AnOwnerGetsExactlyNoScaling()
        {
            // The property that makes a per-component reference authorable at
            // all: a camera sitting at an owner's own reference must leave that
            // owner's authored metres alone.
            StaticInstanceLodSystem.FovTrackingEnabled = true;

            foreach (float reference in new[] { 30f, 50f, 60f, 100f })
            {
                camera.fieldOfView = reference;
                Assert.AreEqual(
                    1f, StaticInstanceLodSystem.ResolveFovFactor(camera, reference), 1e-5f,
                    $"an owner at its own reference ({reference}) must not be scaled");
            }
        }

        [Test]
        public void TwoOwners_AtDifferentReferences_GetDifferentFactorsFromOneCamera()
        {
            // The whole point of a per-component reference FOV: one view, two
            // owners, two answers.
            StaticInstanceLodSystem.FovTrackingEnabled = true;
            camera.fieldOfView = 40f;

            float narrowRef = StaticInstanceLodSystem.ResolveFovFactor(camera, 30f);
            float wideRef = StaticInstanceLodSystem.ResolveFovFactor(camera, 90f);

            Assert.Greater(
                wideRef, narrowRef,
                "an owner authored at a WIDER reference must scale out further " +
                "at the same camera FOV");
        }

        [Test]
        public void TheCameraTerm_ReportsNoFov_ForNullAndOrthographic()
        {
            // `hasFov` is what the no-FOV fallback turns on: in Distance mode its
            // false case means factor 1 (neutral), and in Screen Size mode it
            // means "use the owner's own reference", because
            // there a factor of 1 would be wrong rather than neutral.
            StaticInstanceLodSystem.ResolveCameraFovTerm(null, out bool nullHasFov);
            Assert.IsFalse(nullHasFov, "a null camera has no FOV");

            camera.orthographic = true;
            StaticInstanceLodSystem.ResolveCameraFovTerm(camera, out bool orthoHasFov);
            Assert.IsFalse(orthoHasFov, "an orthographic camera has no usable FOV");
        }

        [Test]
        public void TrackingOff_IsExactlyOne()
        {
            // With tracking off the factor is exactly 1, bit for bit. The jobs
            // select the identity per owner rather than multiplying through, so it
            // needs its own assertion.
            StaticInstanceLodSystem.FovTrackingEnabled = false;
            camera.fieldOfView = 17f;

            Assert.AreEqual(
                1f, StaticInstanceLodSystem.ResolveFovFactor(camera, 120f),
                "tracking off must be exactly 1f, bit for bit");
        }

        [Test]
        public void TheSanitizer_MapsOutOfRangeToTheDefault_NotToTheRangeEdge()
        {
            // ⚠ The edges are the trap this exists for: clamping a
            // hand-edited 0 to 1 degree gives tan(0.5°) ~ 0.0087, which would
            // collapse every threshold to a few percent of the authored value.
            // The default is the only value that means "never chose one".
            Assert.AreEqual(50f, StaticInstanceLodSystem.SanitizeReferenceFov(0f), 0.001f);
            Assert.AreEqual(50f, StaticInstanceLodSystem.SanitizeReferenceFov(-30f), 0.001f);
            Assert.AreEqual(50f, StaticInstanceLodSystem.SanitizeReferenceFov(180f), 0.001f);
            Assert.AreEqual(50f, StaticInstanceLodSystem.SanitizeReferenceFov(float.NaN), 0.001f);

            // In range is left exactly alone.
            Assert.AreEqual(1f, StaticInstanceLodSystem.SanitizeReferenceFov(1f), 0.001f);
            Assert.AreEqual(73f, StaticInstanceLodSystem.SanitizeReferenceFov(73f), 0.001f);
            Assert.AreEqual(179f, StaticInstanceLodSystem.SanitizeReferenceFov(179f), 0.001f);
        }

        [Test]
        public void TheResolver_AnswersADegenerateReferenceWithIdentity_NotTheSanitizedValue()
        {
            // TWO PATHS, TWO SAFE ANSWERS, and this pins the difference so
            // it cannot be "tidied" into one. The scalar resolver says 1f: a
            // caller with a nonsense reference has no opinion about scaling.
            // The REBUILD sanitizes to 50 instead, because there a usable
            // reference is required to compute tan(ref/2) at all.
            StaticInstanceLodSystem.FovTrackingEnabled = true;
            camera.fieldOfView = 40f;

            Assert.AreEqual(
                1f, StaticInstanceLodSystem.ResolveFovFactor(camera, 0f),
                "a degenerate reference must resolve to exactly 1f");
            Assert.AreNotEqual(
                StaticInstanceLodSystem.ResolveFovFactor(camera, 50f),
                StaticInstanceLodSystem.ResolveFovFactor(camera, 0f),
                "the resolver must NOT silently substitute the sanitized default");
        }
    }
}
