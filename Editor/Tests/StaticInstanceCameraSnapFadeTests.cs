using NUnit.Framework;
using Unity.Mathematics;

namespace StaticInstances.Editor.Tests
{
    public sealed class StaticInstanceCameraSnapFadeTests
    {
        [TestCase(5f, 0, 127f)]
        [TestCase(15f, 1, 0f)]
        public void WarpSnapsBothTimeDomainsToNewGoalsWhileOtherViewContinues(float position, int level, float fade)
        {
            using (var target = new StaticInstanceForcedLodFadeTests.Evaluation(StaticInstanceFadeMode.Time, true))
            using (var other = new StaticInstanceForcedLodFadeTests.Evaluation(StaticInstanceFadeMode.Time, true))
            {
                // Start the opposite goal and a genuinely in-progress mesh pair.
                target.Set("CameraPosition", new float3(position < 10 ? 15 : 5, 0, 0));
                target.Step(); other.Step();
                target.Set("CameraPosition", new float3(position, 0, 0));
                other.Set("CameraPosition", new float3(15, 0, 0));
                target.Set("SnapTimeFades", true); target.Step(); other.Step();
                Assert.AreEqual(level, target.Read<byte>("InstanceLevels"));
                Assert.AreEqual(0, target.Read<byte>("CrossfadeValues"));
                Assert.AreEqual(0f, target.Read<float>("MeshFadeProgress"));
                Assert.AreEqual(fade, target.Read<float>("InstanceFades"));
                Assert.Greater(other.Read<byte>("CrossfadeValues"), 0);
                Assert.That(other.Read<float>("InstanceFades"), Is.GreaterThan(0).And.LessThan(127));
            }
        }

        [TestCase(StaticInstanceFadeMode.Distance)]
        [TestCase(StaticInstanceFadeMode.None)]
        public void NonTimeModesDoNotChange(StaticInstanceFadeMode mode)
        {
            using (var target = new StaticInstanceForcedLodFadeTests.Evaluation(mode, true))
            using (var control = new StaticInstanceForcedLodFadeTests.Evaluation(mode, true))
            {
                target.Set("SnapTimeFades", true); target.Step(); control.Step();
                Assert.AreEqual(control.Read<byte>("InstanceLevels"), target.Read<byte>("InstanceLevels"));
                Assert.AreEqual(control.Read<byte>("CrossfadeValues"), target.Read<byte>("CrossfadeValues"));
                Assert.AreEqual(control.Read<float>("InstanceFades"), target.Read<float>("InstanceFades"));
            }
        }
        [Test]
        public void SharedShadowMergeRetainsOtherViewsFinerPairAndVisibility()
        {
            var type = typeof(StaticInstanceRendererSystem).GetNestedType("MergeShadowSliceJob", System.Reflection.BindingFlags.NonPublic);
            object job = System.Activator.CreateInstance(type);
            var arrays = new System.Collections.Generic.Dictionary<string, object>();
            try
            {
                foreach (var field in type.GetFields())
                {
                    if (!field.FieldType.IsGenericType) continue;
                    var array = System.Activator.CreateInstance(field.FieldType, 1, Unity.Collections.Allocator.Temp, Unity.Collections.NativeArrayOptions.ClearMemory);
                    arrays.Add(field.Name, array); field.SetValue(job, array);
                }
                void Put(string name, object value) => arrays[name].GetType().GetProperty("Item").SetValue(arrays[name], value, new object[] { 0 });
                object Read(string name) => arrays[name].GetType().GetProperty("Item").GetValue(arrays[name], new object[] { 0 });
                type.GetField("EvaluateLod").SetValue(job, true); type.GetField("DistanceCull").SetValue(job, true);
                type.GetField("Initialize").SetValue(job, true);
                Put("SourceLevels", (byte)1); Put("SourceCrossfades", (byte)0); Put("SourceFades", 0f);
                type.GetMethod("Execute").Invoke(job, new object[] { 0 });
                type.GetField("Initialize").SetValue(job, false);
                Put("SourceLevels", (byte)0x81); Put("SourceCrossfades", (byte)50); Put("SourceFades", 80f);
                type.GetMethod("Execute").Invoke(job, new object[] { 0 });
                Assert.AreEqual((byte)0x81, Read("TargetLevels"));
                Assert.AreEqual((byte)50, Read("TargetCrossfades"));
                Assert.AreEqual(80f, Read("TargetFades"));
            }
            finally { foreach (System.IDisposable array in arrays.Values) array.Dispose(); }
        }
    }
}
