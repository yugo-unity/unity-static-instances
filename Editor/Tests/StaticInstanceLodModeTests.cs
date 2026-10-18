using NUnit.Framework;
using UnityEngine;

namespace StaticInstances.Editor.Tests
{
    public sealed class StaticInstanceLodModeTests
    {
        private GameObject go;
        private Camera camera;
        private bool savedTracking;

        [SetUp]
        public void SetUp() => savedTracking = StaticInstanceLodSystem.FovTrackingEnabled;

        [TearDown]
        public void TearDown()
        {
            StaticInstanceLodSystem.FovTrackingEnabled = savedTracking;
            if (go != null) Object.DestroyImmediate(go);
            if (camera != null) Object.DestroyImmediate(camera.gameObject);
        }

        [Test]
        public void DistanceIsTheSerializedZeroValue()
        {
            Assert.AreEqual(0, (int)StaticInstanceLodMode.Distance);
            Assert.AreEqual(1, (int)StaticInstanceLodMode.ScreenSize);
        }

        [Test]
        public void NewScatterUsesScreenSizeAndTimeFadeDefaults()
        {
            go = new GameObject("scatter");
            var scatter = go.AddComponent<StaticInstanceScatter>();
            Assert.AreEqual(StaticInstanceLodMode.ScreenSize, scatter.LodMode);
            Assert.AreEqual(StaticInstanceFadeMode.Time, scatter.FadeMode);
            Assert.IsTrue(scatter.LodRatios == null || scatter.LodRatios.Length == 0);
        }

        [Test]
        public void ScreenSizeLadderReadsRatiosAndDistanceLadderReadsDistances()
        {
            Assert.IsTrue(StaticInstanceScatter.HasLadderFor(
                2, StaticInstanceLodMode.ScreenSize, null, new[] { 0.2f, 0.1f }));
            Assert.IsFalse(StaticInstanceScatter.HasLadderFor(
                2, StaticInstanceLodMode.ScreenSize, new[] { 10f, 20f }, null));
            Assert.IsTrue(StaticInstanceScatter.HasLadderFor(
                2, StaticInstanceLodMode.Distance, new[] { 10f, 20f }, null));
        }

        [Test]
        public void PublicWritersAuthorAWorkingScreenSizeLadder()
        {
            go = new GameObject("scatter");
            var scatter = go.AddComponent<StaticInstanceScatter>();
            var ratios = new[] { 0.2f, 0.1f };
            scatter.SetLodRatios(ratios);
            scatter.LodMode = StaticInstanceLodMode.ScreenSize;
            Assert.AreEqual(ratios, scatter.LodRatios);
            Assert.IsTrue(StaticInstanceScatter.HasLadderFor(
                2, scatter.LodMode, scatter.LodDistances, scatter.LodRatios));
        }

        [Test]
        public void ReferenceFovUsesThePerspectiveCameraOrDefault()
        {
            var cameraObject = new GameObject("camera");
            camera = cameraObject.AddComponent<Camera>();
            camera.orthographic = false;
            camera.fieldOfView = 73f;
            Assert.AreEqual(73f, StaticInstanceLodSystem.SeedReferenceFovFrom(camera), 0.001f);
            camera.orthographic = true;
            Assert.AreEqual(StaticInstanceLodSystem.DefaultReferenceFovDegrees,
                StaticInstanceLodSystem.SeedReferenceFovFrom(camera), 0.001f);
            Assert.AreEqual(StaticInstanceLodSystem.DefaultReferenceFovDegrees,
                StaticInstanceLodSystem.SeedReferenceFovFrom(null), 0.001f);
        }

        [Test]
        public void DisabledFovTrackingLeavesEvaluationFactorAtOne()
        {
            StaticInstanceLodSystem.FovTrackingEnabled = false;
            var cameraObject = new GameObject("camera");
            camera = cameraObject.AddComponent<Camera>();
            camera.fieldOfView = 24f;
            Assert.AreEqual(1f, StaticInstanceLodSystem.ResolveFovFactorForDiagnostics(
                camera, StaticInstanceLodSystem.DefaultReferenceFovDegrees));
        }
    }
}
