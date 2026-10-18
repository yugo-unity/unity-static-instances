using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace StaticInstances.Editor.Tests
{
    public sealed class StaticInstanceForcedLodFadeTests
    {
        static IEnumerable Cases()
        {
            foreach (StaticInstanceFadeMode mode in Enum.GetValues(typeof(StaticInstanceFadeMode)))
                foreach (bool global in new[] { false, true })
                    foreach (bool enabled in new[] { false, true })
                        yield return new object[] { mode, global, enabled };
        }

        // Invoke the actual per-view job, including both fade domains, without a
        // BRG/Player fixture or a new production test hook.
        internal sealed class Evaluation : IDisposable
        {
            readonly Type type = typeof(StaticInstanceRendererSystem).GetNestedType("DrivingViewEvalJob", BindingFlags.NonPublic);
            readonly object job;
            readonly Dictionary<string, object> arrays = new Dictionary<string, object>();
            public Evaluation(StaticInstanceFadeMode mode, bool enabled)
            {
                job = Activator.CreateInstance(type);
                foreach (var field in type.GetFields())
                {
                    if (!field.FieldType.IsGenericType || field.FieldType.GetGenericTypeDefinition() != typeof(NativeArray<>)) continue;
                    var array = Activator.CreateInstance(field.FieldType, 1, Allocator.Temp, NativeArrayOptions.ClearMemory);
                    arrays.Add(field.Name, array);
                    field.SetValue(job, array);
                }
                Set("DistanceCull", true); Set("EvaluateLod", true);
                Set("DistanceScale", 1f); Set("LodBias", 1f); Set("CameraPosition", new float3(9, 0, 0));
                Set("CrossFadeEnabled", enabled); Set("SnapTimeFades", !enabled);
                Set("AnyTimeFade", mode == StaticInstanceFadeMode.Time && enabled);
                Set("DeltaTime", .1f); Set("GlobalRate", 127f); Set("ForcedLevel", -1);
                Put("CullThresholdsSq", 100f); Put("FadeRates", mode == StaticInstanceFadeMode.Time && enabled ? 127f : float.MaxValue);
                Put("InstanceFades", 50f); Put("MeshFadeFrom", (byte)0x80);
                Parameters = new OwnerLodParams { Policy = (byte)LodSelectionPolicy.PerInstance,
                    MaxLevel = 1, ForcedLevel = -1, Boundaries = new float3(10, 20, 30), RefTan = 1,
                    DistanceFade = (byte)(mode == StaticInstanceFadeMode.Distance && enabled ? 1 : 0),
                    TimeFade = (byte)(mode == StaticInstanceFadeMode.Time && enabled ? 1 : 0),
                    CrossFadeWidth = mode == StaticInstanceFadeMode.Distance && enabled ? 2f : 0f };
            }
            public OwnerLodParams Parameters;
            public void Set(string name, object value) => type.GetField(name).SetValue(job, value);
            public void Put(string name, object value) => arrays[name].GetType().GetProperty("Item").SetValue(arrays[name], value, new object[] { 0 });
            public T Read<T>(string name) => (T)arrays[name].GetType().GetProperty("Item").GetValue(arrays[name], new object[] { 0 });
            public void Step() { Put("OwnerParams", Parameters); type.GetMethod("Execute").Invoke(job, new object[] { 0 }); }
            public void StepWithRangeProof(bool rejected) { Put("OwnerParams", Parameters); type.GetMethod("EvaluateInstance").Invoke(job, new object[] { 0, rejected }); }
            public void Dispose() { foreach (IDisposable array in arrays.Values) array.Dispose(); }
        }

        [TestCaseSource(nameof(Cases))]
        public void ForceOnlySnapsLodAndUnforceRestoresAuthoredFade(StaticInstanceFadeMode mode, bool global, bool enabled)
        {
            using (var forced = new Evaluation(mode, enabled))
            using (var control = new Evaluation(mode, enabled))
            {
                // First advance an already-in-progress Draw Range fade, then pin LOD.
                forced.Step(); control.Step();
                if (global) forced.Set("ForcedLevel", 1);
                else forced.Parameters.ForcedLevel = 1;
                for (int frame = 0; frame < 3; frame++)
                {
                    forced.Step(); control.Step();
                    Assert.AreEqual(control.Read<float>("InstanceFades"), forced.Read<float>("InstanceFades"), "Force must not reset, stop, or alter Draw Range progression.");
                    Assert.AreEqual(1, forced.Read<byte>("InstanceLevels"));
                    Assert.AreEqual(0, forced.Read<byte>("CrossfadeValues"));
                }
                if (enabled && mode != StaticInstanceFadeMode.None)
                    Assert.That(forced.Read<float>("InstanceFades"), Is.GreaterThan(0).And.LessThan(127), "The Draw Range fade must actually be intermediate.");
                forced.Set("ForcedLevel", -1); forced.Parameters.ForcedLevel = -1;
                forced.Step();
                Assert.AreEqual(0, forced.Read<byte>("InstanceLevels") & 3);
                Assert.AreEqual(enabled && mode != StaticInstanceFadeMode.None, forced.Read<byte>("CrossfadeValues") > 0);
            }
        }

        [TestCase(1, 1, StaticInstanceFadeMode.Time, StaticInstanceFadeMode.Distance, false)]
        [TestCase(0, 2, StaticInstanceFadeMode.Time, StaticInstanceFadeMode.Distance, false)]
        [TestCase(0, -1, StaticInstanceFadeMode.Time, StaticInstanceFadeMode.Distance, true)]
        [TestCase(0, -1, StaticInstanceFadeMode.Time, StaticInstanceFadeMode.None, false)]
        public void EffectiveSelectionUsesResolvedValues(int forceA, int forceB,
            StaticInstanceFadeMode modeA, StaticInstanceFadeMode modeB, bool expectedMixed)
        {
            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
            try
            {
                var owners = new StaticInstanceScatter[2];
                for (int i = 0; i < owners.Length; i++)
                {
                    var go = new GameObject("Selection");
                    UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, scene);
                    owners[i] = go.AddComponent<StaticInstanceScatter>();
                    owners[i].ForcedLodLevel = i == 0 ? forceA : forceB;
                    owners[i].FadeMode = i == 0 ? modeA : modeB;
                }
                using (var data = new SerializedObject(owners))
                {
                    var effective = StaticInstanceEditorCommon.ResolveLodFadeSelection(
                        data.FindProperty("forcedLodLevel"), data.FindProperty("fadeMode"), -1, true, out bool mixed);
                    Assert.AreEqual(expectedMixed, mixed);
                    Assert.AreEqual(StaticInstanceFadeMode.None, effective);
                    Assert.IsFalse(data.hasModifiedProperties);
                }
            }
            finally { UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(scene); }
        }
        [TestCase(StaticInstanceFadeMode.None)]
        [TestCase(StaticInstanceFadeMode.Time)]
        [TestCase(StaticInstanceFadeMode.Distance)]
        public void ForcePreservesAuthoredMode(StaticInstanceFadeMode mode)
        {
            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
            try
            {
                var go = new GameObject("Force contract");
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, scene);
                var scatter = go.AddComponent<StaticInstanceScatter>();
                scatter.FadeMode = mode;
                foreach (int level in new[] { 0, 1, -1 })
                {
                    scatter.ForcedLodLevel = level;
                    Assert.AreEqual(mode, scatter.FadeMode);
                    Assert.AreEqual(StaticInstanceLodSystem.ResolveFadeMode(mode), scatter.EffectiveFadeMode);
                    Assert.AreEqual(level < 0 ? mode : StaticInstanceFadeMode.None,
                        StaticInstanceEditorCommon.ResolveLodFadeMode(mode, level, -1, true));
                }
                Assert.AreEqual(StaticInstanceFadeMode.None, StaticInstanceEditorCommon.ResolveLodFadeMode(mode, -1, -1, false));
            }
            finally { UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(scene); }
        }
    }
}
