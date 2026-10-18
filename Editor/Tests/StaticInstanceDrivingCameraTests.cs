using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace StaticInstances.Editor.Tests
{
    public sealed class StaticInstanceDrivingCameraTests
    {
        readonly List<GameObject> objects = new List<GameObject>();
        readonly List<Camera> previousMain = new List<Camera>();
        static readonly FieldInfo FrameCache = typeof(StaticInstanceLodSystem).GetField(
            "resolvedDrivingFrame", BindingFlags.Static | BindingFlags.NonPublic);

        [SetUp]
        public void SetUp()
        {
            StaticInstanceLodSystem.ClearDrivingCameras();
            ExpireFrame();
#if UNITY_6000_4_OR_NEWER
            // 6000.4 deprecated FindObjectsSortMode.
            Camera[] cameras = Object.FindObjectsByType<Camera>();
#else
            Camera[] cameras = Object.FindObjectsByType<Camera>(FindObjectsSortMode.None);
#endif
            foreach (Camera camera in cameras)
                if (camera.CompareTag("MainCamera"))
                {
                    previousMain.Add(camera);
                    camera.tag = "Untagged";
                }
        }

        [TearDown]
        public void TearDown()
        {
            StaticInstanceLodSystem.ClearDrivingCameras();
            foreach (GameObject obj in objects)
                if (obj != null) Object.DestroyImmediate(obj);
            objects.Clear();
            foreach (Camera camera in previousMain)
                if (camera != null) camera.tag = "MainCamera";
            previousMain.Clear();
            ExpireFrame();
        }

        Camera MakeCamera(string name, bool main = false)
        {
            var obj = new GameObject(name);
            objects.Add(obj);
            var camera = obj.AddComponent<Camera>();
            if (main) camera.tag = "MainCamera";
            return camera;
        }

        static void ExpireFrame() => FrameCache.SetValue(null, int.MinValue);

        [Test]
        public void DestroyedLastRegistration_FirstResolutionUsesMainInThatFrame()
        {
            Camera main = MakeCamera("fallback", true);
            main.transform.position = new Vector3(3, 4, 5);
            Camera registered = MakeCamera("registered");
            StaticInstanceLodSystem.RegisterDrivingCamera(registered);
            int generation = StaticInstanceLodSystem.DrivingCameraGeneration;
            Object.DestroyImmediate(registered.gameObject);

            Assert.AreEqual(1, StaticInstanceLodSystem.ResolveDrivingCameras());
            Assert.AreSame(main, StaticInstanceLodSystem.GetDrivingCamera(0));
            Assert.AreEqual(main.transform.position, StaticInstanceLodSystem.GetDrivingCameraPosition(0));
            Assert.IsTrue(StaticInstanceLodSystem.DrivingCamerasAreDefault);
            Assert.AreEqual(generation + 1, StaticInstanceLodSystem.DrivingCameraGeneration);
            Assert.AreEqual(1, StaticInstanceLodSystem.ResolveDrivingCameras());
            Assert.AreEqual(generation + 1, StaticInstanceLodSystem.DrivingCameraGeneration);
        }

        [Test]
        public void DestroyedRegistration_WithoutMainResolvesEmptyOnce()
        {
            Camera registered = MakeCamera("registered");
            StaticInstanceLodSystem.RegisterDrivingCamera(registered);
            int generation = StaticInstanceLodSystem.DrivingCameraGeneration;
            Object.DestroyImmediate(registered.gameObject);
            Assert.AreEqual(0, StaticInstanceLodSystem.ResolveDrivingCameras());
            Assert.IsTrue(StaticInstanceLodSystem.DrivingCamerasAreDefault);
            Assert.AreEqual(generation + 1, StaticInstanceLodSystem.DrivingCameraGeneration);
            ExpireFrame();
            Assert.AreEqual(0, StaticInstanceLodSystem.ResolveDrivingCameras());
            Assert.AreEqual(generation + 1, StaticInstanceLodSystem.DrivingCameraGeneration);
        }

        [Test]
        public void ResolvedFrame_KeepsSnapshotUntilNextResolutionFrame()
        {
            Camera main = MakeCamera("fallback", true);
            Camera registered = MakeCamera("registered");
            registered.transform.position = new Vector3(7, 8, 9);
            StaticInstanceLodSystem.RegisterDrivingCamera(registered);
            Assert.AreEqual(1, StaticInstanceLodSystem.ResolveDrivingCameras());
            Vector3 snapshot = StaticInstanceLodSystem.GetDrivingCameraPosition(0);
            int generation = StaticInstanceLodSystem.DrivingCameraGeneration;
            Object.DestroyImmediate(registered.gameObject);
            Assert.AreEqual(1, StaticInstanceLodSystem.ResolveDrivingCameras());
            Assert.AreSame(registered, StaticInstanceLodSystem.GetDrivingCamera(0));
            Assert.AreEqual(snapshot, StaticInstanceLodSystem.GetDrivingCameraPosition(0));
            Assert.AreEqual(generation, StaticInstanceLodSystem.DrivingCameraGeneration);

            // Simulate the next frame without changing production caching semantics.
            ExpireFrame();
            Assert.AreEqual(1, StaticInstanceLodSystem.ResolveDrivingCameras());
            Assert.AreSame(main, StaticInstanceLodSystem.GetDrivingCamera(0));
            Assert.AreEqual(generation + 1, StaticInstanceLodSystem.DrivingCameraGeneration);
        }

        [Test]
        public void DestroyedEntries_PreserveLiveOrderAndExcludeMain()
        {
            MakeCamera("fallback", true);
            Camera a = MakeCamera("a"), dead = MakeCamera("dead"), b = MakeCamera("b");
            StaticInstanceLodSystem.RegisterDrivingCamera(a);
            StaticInstanceLodSystem.RegisterDrivingCamera(dead);
            StaticInstanceLodSystem.RegisterDrivingCamera(b);
            int generation = StaticInstanceLodSystem.DrivingCameraGeneration;
            Object.DestroyImmediate(dead.gameObject);
            Assert.AreEqual(2, StaticInstanceLodSystem.ResolveDrivingCameras());
            Assert.AreSame(a, StaticInstanceLodSystem.GetDrivingCamera(0));
            Assert.AreSame(b, StaticInstanceLodSystem.GetDrivingCamera(1));
            Assert.AreEqual(generation + 1, StaticInstanceLodSystem.DrivingCameraGeneration);
            Assert.IsFalse(StaticInstanceLodSystem.DrivingCamerasAreDefault);
        }

        [Test]
        public void RegistrationChanges_InvalidateSameFrameCacheAndDuplicatesDoNot()
        {
            Camera main = MakeCamera("fallback", true), a = MakeCamera("a"), b = MakeCamera("b");
            Assert.AreEqual(1, StaticInstanceLodSystem.ResolveDrivingCameras());
            int generation = StaticInstanceLodSystem.DrivingCameraGeneration;
            Assert.IsTrue(StaticInstanceLodSystem.RegisterDrivingCamera(a));
            Assert.AreEqual(generation + 1, StaticInstanceLodSystem.DrivingCameraGeneration);
            Assert.AreSame(a, ResolveFirst());
            Assert.IsTrue(StaticInstanceLodSystem.RegisterDrivingCamera(a));
            Assert.AreEqual(generation + 1, StaticInstanceLodSystem.DrivingCameraGeneration);
            Assert.IsFalse(StaticInstanceLodSystem.UnregisterDrivingCamera(b));
            Assert.AreEqual(generation + 1, StaticInstanceLodSystem.DrivingCameraGeneration);
            StaticInstanceLodSystem.RegisterDrivingCamera(b);
            StaticInstanceLodSystem.UnregisterDrivingCamera(a);
            Assert.AreSame(b, ResolveFirst());
            StaticInstanceLodSystem.RegisterDrivingCamera(a);
            Assert.AreEqual(2, StaticInstanceLodSystem.ResolveDrivingCameras());
            Assert.AreSame(b, StaticInstanceLodSystem.GetDrivingCamera(0));
            Assert.AreSame(a, StaticInstanceLodSystem.GetDrivingCamera(1));
            StaticInstanceLodSystem.ClearDrivingCameras();
            Assert.AreSame(main, ResolveFirst());
        }

        static Camera ResolveFirst()
        {
            Assert.Greater(StaticInstanceLodSystem.ResolveDrivingCameras(), 0);
            return StaticInstanceLodSystem.GetDrivingCamera(0);
        }

        [Test]
        public void ResolvedRegistryChanges_RefreshCameraAndSharedShadowSliceCounts()
        {
            var type = typeof(StaticInstanceRendererSystem);
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            string[] names = { "drivingGenerationSnapshot", "drivingCameraCountSnapshot",
                "drivingSliceCount", "shadowSliceIndex", "combinedDataDirty" };
            var saved = new Dictionary<FieldInfo, object>();
            foreach (string name in names)
            {
                FieldInfo field = type.GetField(name, flags);
                saved.Add(field, field.GetValue(null));
            }
            void Refresh(int count, int slices)
            {
                StaticInstanceLodSystem.ResolveDrivingCameras();
                type.GetMethod("RefreshDrivingSlices", flags).Invoke(null, null);
                Assert.AreEqual(count, type.GetField("drivingCameraCountSnapshot", flags).GetValue(null));
                Assert.AreEqual(slices, type.GetField("drivingSliceCount", flags).GetValue(null));
                Assert.AreEqual(slices == 1 ? 0 : count, type.GetField("shadowSliceIndex", flags).GetValue(null));
                Assert.AreEqual(StaticInstanceLodSystem.DrivingCameraGeneration,
                    type.GetField("drivingGenerationSnapshot", flags).GetValue(null));
            }
            try
            {
                MakeCamera("fallback", true);
                var a = MakeCamera("a"); var b = MakeCamera("b");
                StaticInstanceLodSystem.RegisterDrivingCamera(a);
                StaticInstanceLodSystem.RegisterDrivingCamera(b);
                Refresh(2, 3);
                Assert.IsTrue((bool)type.GetField("combinedDataDirty", flags).GetValue(null));
                type.GetField("combinedDataDirty", flags).SetValue(null, false);
                StaticInstanceLodSystem.RegisterDrivingCamera(a);
                Refresh(2, 3);
                Assert.IsFalse((bool)type.GetField("combinedDataDirty", flags).GetValue(null));
                Object.DestroyImmediate(a.gameObject); Object.DestroyImmediate(b.gameObject);
                ExpireFrame();
                Refresh(1, 1);
                Assert.IsTrue(StaticInstanceLodSystem.DrivingCamerasAreDefault);
                Assert.IsTrue((bool)type.GetField("combinedDataDirty", flags).GetValue(null));
            }
            finally
            {
                foreach (var item in saved) item.Key.SetValue(null, item.Value);
            }
        }
        [Test]
        public void FourCameraLimit_RejectsFifthThenReusesRemovedSlot()
        {
            var cameras = new Camera[5];
            for (int i = 0; i < cameras.Length; i++) cameras[i] = MakeCamera("view" + i);
            for (int cycle = 0; cycle < 4; cycle++)
            {
                for (int i = 0; i < 4; i++) Assert.IsTrue(StaticInstanceLodSystem.RegisterDrivingCamera(cameras[i]));
                int generation = StaticInstanceLodSystem.DrivingCameraGeneration;
                Assert.IsTrue(StaticInstanceLodSystem.RegisterDrivingCamera(cameras[2]));
                LogAssert.Expect(LogType.Error, new Regex("4 driving cameras are already registered"));
                Assert.IsFalse(StaticInstanceLodSystem.RegisterDrivingCamera(cameras[4]));
                Assert.AreEqual(generation, StaticInstanceLodSystem.DrivingCameraGeneration);
                Assert.AreEqual(4, StaticInstanceLodSystem.ResolveDrivingCameras());
                Assert.IsTrue(StaticInstanceLodSystem.UnregisterDrivingCamera(cameras[1]));
                Assert.IsTrue(StaticInstanceLodSystem.RegisterDrivingCamera(cameras[4]));
                Assert.AreEqual(4, StaticInstanceLodSystem.ResolveDrivingCameras());
                Assert.AreSame(cameras[4], StaticInstanceLodSystem.GetDrivingCamera(3));
                StaticInstanceLodSystem.ClearDrivingCameras();
                Assert.AreEqual(0, StaticInstanceLodSystem.ResolveDrivingCameras());
            }
        }
        [Test]
        public void SnapRequestsCoalesceAndRespectPositionSnapshot()
        {
            var camera = MakeCamera("driver");
            StaticInstanceLodSystem.RegisterDrivingCamera(camera);
            Assert.IsTrue(StaticInstanceLodSystem.SnapFades(camera));
            Assert.IsTrue(StaticInstanceLodSystem.SnapFades(camera));
            StaticInstanceLodSystem.ResolveDrivingCameras();
            Assert.IsTrue(StaticInstanceLodSystem.ConsumeFadeSnap(camera, Time.frameCount));
            Assert.IsFalse(StaticInstanceLodSystem.ConsumeFadeSnap(camera, Time.frameCount));
            camera.transform.position = Vector3.one;
            Assert.IsTrue(StaticInstanceLodSystem.SnapFades(camera));
            Assert.IsFalse(StaticInstanceLodSystem.ConsumeFadeSnap(camera, Time.frameCount));
            Assert.AreEqual(Vector3.zero, StaticInstanceLodSystem.GetDrivingCameraPosition(0));
            Assert.IsTrue(StaticInstanceLodSystem.ConsumeFadeSnap(camera, Time.frameCount + 1));
        }

        [Test]
        public void SnapIdentitySurvivesOtherRemovalButNotOwnUnregisterOrReset()
        {
            var a = MakeCamera("first"); var b = MakeCamera("second"); var c = MakeCamera("replacement");
            StaticInstanceLodSystem.RegisterDrivingCamera(a); StaticInstanceLodSystem.RegisterDrivingCamera(b);
            Assert.IsTrue(StaticInstanceLodSystem.SnapFades(b));
            StaticInstanceLodSystem.UnregisterDrivingCamera(a); StaticInstanceLodSystem.RegisterDrivingCamera(c);
            Assert.IsFalse(StaticInstanceLodSystem.ConsumeFadeSnap(c, Time.frameCount + 1));
            Assert.IsTrue(StaticInstanceLodSystem.ConsumeFadeSnap(b, Time.frameCount + 1));
            StaticInstanceLodSystem.SnapFades(b); StaticInstanceLodSystem.UnregisterDrivingCamera(b);
            StaticInstanceLodSystem.RegisterDrivingCamera(b);
            Assert.IsFalse(StaticInstanceLodSystem.ConsumeFadeSnap(b, Time.frameCount + 1));
            StaticInstanceLodSystem.SnapFades(b); StaticInstanceLodSystem.ClearDrivingCameras();
            StaticInstanceLodSystem.RegisterDrivingCamera(b);
            Assert.IsFalse(StaticInstanceLodSystem.ConsumeFadeSnap(b, Time.frameCount + 1));
        }

        [Test]
        public void SnapAcceptsOnlyLiveDrivingIdentityIncludingMainFallback()
        {
            var main = MakeCamera("main", true); var other = MakeCamera("other");
            Assert.IsFalse(StaticInstanceLodSystem.SnapFades(null));
            Assert.IsFalse(StaticInstanceLodSystem.SnapFades(other));
            Assert.IsTrue(StaticInstanceLodSystem.SnapFades(main));
            StaticInstanceLodSystem.ClearDrivingCameras();
            Assert.IsFalse(StaticInstanceLodSystem.ConsumeFadeSnap(main, Time.frameCount + 1));
            StaticInstanceLodSystem.RegisterDrivingCamera(other);
            Assert.IsFalse(StaticInstanceLodSystem.SnapFades(main));
            StaticInstanceLodSystem.SnapFades(other); Object.DestroyImmediate(other.gameObject);
            Assert.IsFalse(StaticInstanceLodSystem.SnapFades(other));
            ExpireFrame(); StaticInstanceLodSystem.ResolveDrivingCameras();
            Assert.IsTrue(StaticInstanceLodSystem.SnapFades(main));
        }

        [Test]
        public void SnapRequestAndConsumeAllocateNothingAfterWarmup()
        {
            var camera = MakeCamera("driver"); StaticInstanceLodSystem.RegisterDrivingCamera(camera);
            for (int i = 0; i < 8; i++) { StaticInstanceLodSystem.SnapFades(camera); StaticInstanceLodSystem.ConsumeFadeSnap(camera, Time.frameCount + 1); }
            long before = System.GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 128; i++) { StaticInstanceLodSystem.SnapFades(camera); StaticInstanceLodSystem.ConsumeFadeSnap(camera, Time.frameCount + 1); }
            Assert.AreEqual(0, System.GC.GetAllocatedBytesForCurrentThread() - before);
        }
        [Test]
        public void SchedulingFastPathsDoNotConsumePendingRequests()
        {
            var camera = MakeCamera("driver"); StaticInstanceLodSystem.RegisterDrivingCamera(camera);
            Assert.IsTrue(StaticInstanceLodSystem.SnapFades(camera));
            var type = typeof(StaticInstanceRendererSystem);
            var distance = type.GetField("anyDistanceCull", BindingFlags.Static | BindingFlags.NonPublic);
            var lod = type.GetField("anyPerInstanceLod", BindingFlags.Static | BindingFlags.NonPublic);
            var frame = type.GetField("lastFadeAnimationFrame", BindingFlags.Static | BindingFlags.NonPublic);
            var schedule = type.GetMethod("ScheduleDrivingViewEval", BindingFlags.Static | BindingFlags.NonPublic);
            object oldDistance = distance.GetValue(null), oldLod = lod.GetValue(null), oldFrame = frame.GetValue(null);
            try
            {
                distance.SetValue(null, false); lod.SetValue(null, false);
                schedule.Invoke(null, new object[] { default(UnityEngine.Rendering.BatchCullingContext) });
                distance.SetValue(null, true); frame.SetValue(null, Time.frameCount);
                schedule.Invoke(null, new object[] { default(UnityEngine.Rendering.BatchCullingContext) });
                Assert.IsTrue(StaticInstanceLodSystem.ConsumeFadeSnap(camera, Time.frameCount + 1));
            }
            finally { distance.SetValue(null, oldDistance); lod.SetValue(null, oldLod); frame.SetValue(null, oldFrame); }
        }

        [Test]
        public void ReplacingFallbackRegistrationDropsItsOldRequest()
        {
            var main = MakeCamera("main", true); var other = MakeCamera("other");
            StaticInstanceLodSystem.SnapFades(main);
            StaticInstanceLodSystem.RegisterDrivingCamera(other);
            StaticInstanceLodSystem.UnregisterDrivingCamera(other);
            Assert.IsFalse(StaticInstanceLodSystem.ConsumeFadeSnap(main, Time.frameCount + 1));
        }
    }
}