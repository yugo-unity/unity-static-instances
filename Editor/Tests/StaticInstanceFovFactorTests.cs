using NUnit.Framework;
using UnityEngine;

namespace StaticInstances.Editor.Tests
{
    // The runtime FOV factor. What these pin is the CONTRACT the FOV
    // policy rests on, not the arithmetic for its own sake:
    //
    // (1) OFF IS BYTE-IDENTICAL. The feature is off by default,
    //     and every call site multiplies by the factor
    //     unconditionally rather than branching, so "off costs nothing" is
    //     only true if the factor is EXACTLY 1f, bit for bit. `Assert.AreEqual`
    //     on floats would pass for a value that is merely close; these compare
    //     the bits, because a factor of 1.0000001 would silently move every
    //     threshold with FOV tracking off.
    //
    // (2) ORTHOGRAPHIC IS ASSERTED OUT, NOT ASSUMED. Ortho is out
    //     of scope because a FOV factor cannot express it. That is a claim
    //     about behaviour, so it is tested rather than commented.
    //
    // (3) THE DIRECTION. f > 1 when the camera zooms IN — this is the
    //     worst-case sign, and getting it
    //     backwards would make the whole feature push thresholds the wrong way
    //     while still "working".
    public sealed class StaticInstanceFovFactorTests
    {
        // 60 is the reference FOV passed per call throughout this file; it
        // fixes every expected number.
        private const float Reference = 60f;

        private Camera camera;
        private GameObject host;
        private bool savedEnabled;

        [SetUp]
        public void SetUp()
        {
            savedEnabled = StaticInstanceLodSystem.FovTrackingEnabled;
            host = new GameObject("FovFactorTestCamera");
            camera = host.AddComponent<Camera>();
        }

        [TearDown]
        public void TearDown()
        {
            StaticInstanceLodSystem.FovTrackingEnabled = savedEnabled;
            if (host != null)
                Object.DestroyImmediate(host);
        }

        private static void AssertExactlyOne(float value, string what)
        {
            Assert.AreEqual(
                System.BitConverter.SingleToInt32Bits(1f),
                System.BitConverter.SingleToInt32Bits(value),
                $"{what} must be EXACTLY 1f, bit for bit — every call site " +
                "multiplies by it unconditionally, so anything else moves " +
                "thresholds with FOV tracking off.");
        }

        [Test]
        public void Disabled_IsExactlyOne_AtEveryFieldOfView()
        {
            StaticInstanceLodSystem.FovTrackingEnabled = false;

            foreach (float fov in new[] { 1f, 20f, 60f, 120f, 179f })
            {
                camera.fieldOfView = fov;
                AssertExactlyOne(
                    StaticInstanceLodSystem.ResolveFovFactor(camera, Reference),
                    $"The factor at fov {fov} with tracking OFF");
            }
        }

        [Test]
        public void Orthographic_IsExactlyOne_EvenWhileTrackingIsOn()
        {
            StaticInstanceLodSystem.FovTrackingEnabled = true;
            camera.orthographic = true;
            camera.fieldOfView = 20f;

            AssertExactlyOne(
                StaticInstanceLodSystem.ResolveFovFactor(camera, Reference),
                "The factor for an ORTHOGRAPHIC camera");
        }

        [Test]
        public void NullCamera_IsExactlyOne()
        {
            StaticInstanceLodSystem.FovTrackingEnabled = true;

            AssertExactlyOne(
                StaticInstanceLodSystem.ResolveFovFactor(null, Reference),
                "The factor for a null camera (the no-driving-camera fallback)");
        }

        [Test]
        public void AtTheReferenceFov_IsExactlyOne()
        {
            StaticInstanceLodSystem.FovTrackingEnabled = true;
            camera.fieldOfView = Reference;

            AssertExactlyOne(
                StaticInstanceLodSystem.ResolveFovFactor(camera, Reference),
                "The factor at exactly the reference FOV");
        }

        [Test]
        public void ZoomingIn_PushesThresholdsOut_AndZoomingOutPullsThemIn()
        {
            StaticInstanceLodSystem.FovTrackingEnabled = true;

            camera.fieldOfView = 30f;
            float zoomedIn = StaticInstanceLodSystem.ResolveFovFactor(camera, Reference);
            camera.fieldOfView = 120f;
            float zoomedOut = StaticInstanceLodSystem.ResolveFovFactor(camera, Reference);

            Assert.Greater(
                zoomedIn, 1f,
                "A NARROWED fov must give f > 1 so thresholds move further " +
                "out — this is the f > 1 worst-case direction.");
            Assert.Less(
                zoomedOut, 1f,
                "A WIDENED fov must give f < 1, pulling thresholds in.");
            Assert.AreEqual(
                Mathf.Tan(30f * Mathf.Deg2Rad) / Mathf.Tan(15f * Mathf.Deg2Rad),
                zoomedIn, 1e-5f,
                "f must be tan(reference/2) / tan(fov/2).");
        }

        [Test]
        public void DegenerateAngles_AreExactlyOne_RatherThanInfinite()
        {
            StaticInstanceLodSystem.FovTrackingEnabled = true;

            // Camera.fieldOfView clamps, so the degenerate cases this guards
            // reach the resolver through the REFERENCE argument, passed in per call
            // (per-component authoring). An unguarded 0 would divide into
            // infinity and blank the scene.
            camera.fieldOfView = 60f;
            foreach (float reference in new[] { 0f, -10f, 180f, 400f })
            {
                AssertExactlyOne(
                    StaticInstanceLodSystem.ResolveFovFactor(camera, reference),
                    $"The factor for a degenerate reference FOV {reference}");
            }
        }
    }
}
