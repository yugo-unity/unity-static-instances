using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace StaticInstances.Editor.Tests
{
    public sealed class StaticInstanceLodBiasTests
    {
        [Test]
        public void NeutralBiasComposesToTheBaseEquationsBitForBit()
        {
            foreach (bool screen in new[] { false, true })
            foreach (bool applies in new[] { false, true })
            foreach (bool hasFov in new[] { false, true })
            foreach (float scale in new[] { 0f, .7f, 1f, 2f, float.NaN, float.PositiveInfinity })
            foreach (float reference in new[] { 0f, .41421356f, 1f, float.NaN })
            {
                const float camera = 1.7320508f, radius = 3.25f;
                float factor = math.select(1f, reference * camera, applies);
                float boundary = scale * factor, cull = factor;
                if (screen)
                {
                    float term = hasFov ? camera : 1f / math.max(reference, 1e-6f);
                    boundary = scale * radius * term; cull = term;
                }
                StaticInstanceCullingBurst.ComposeEvaluationScales(scale, 1f, reference,
                    screen, radius, camera, applies, hasFov, out float actualBoundary, out float actualCull);
                Assert.AreEqual(math.asuint(boundary), math.asuint(actualBoundary));
                Assert.AreEqual(math.asuint(cull), math.asuint(actualCull));
            }
        }

        [TestCase(false, false)] [TestCase(false, true)]
        [TestCase(true, false)] [TestCase(true, true)]
        public void BiasMultipliesBothScalesWithoutChangingFovRules(bool screen, bool hasFov)
        {
            StaticInstanceCullingBurst.ComposeEvaluationScales(.75f, 1f, .5f, screen,
                3f, 2f, hasFov, hasFov, out float b, out float c);
            foreach (float bias in new[] { 0f, .5f, 2f, 4f })
            {
                StaticInstanceCullingBurst.ComposeEvaluationScales(.75f, bias, .5f, screen,
                    3f, 2f, hasFov, hasFov, out float actualB, out float actualC);
                Assert.AreEqual(b * bias, actualB); Assert.AreEqual(c * bias, actualC);
            }
        }

        [TestCase(false)] [TestCase(true)]
        public void ActualEvaluationScalesLadderAndDrawRange(bool screen)
        {
            using (var job = new StaticInstanceForcedLodFadeTests.Evaluation(StaticInstanceFadeMode.None, true))
            {
                job.Parameters.ScreenSize = (byte)(screen ? 1 : 0);
                job.Put("ScreenRadii", 1f); job.Set("CameraFovTerm", 1f);
                job.Set("LodBias", .5f); job.Step();
                Assert.AreEqual(1, job.Read<byte>("InstanceLevels"));
                Assert.AreEqual(0, job.Read<float>("InstanceFades"));
                job.Set("LodBias", 2f); job.Step();
                Assert.AreEqual(0, job.Read<byte>("InstanceLevels"));
                Assert.AreEqual(127, job.Read<float>("InstanceFades"));
                job.Put("MinThresholdsSq", 64f); job.Step();
                Assert.AreEqual(0, job.Read<float>("InstanceFades"), "Scaled near bound also applies.");
            }
        }

        [Test]
        public void ScreenSizeAbsoluteBoundsStillComposeAfterBias()
        {
            using (var job = new StaticInstanceForcedLodFadeTests.Evaluation(StaticInstanceFadeMode.None, true))
            {
                job.Parameters.ScreenSize = 1; job.Put("ScreenRadii", 1f);
                job.Set("LodBias", 2f); job.Parameters.AbsoluteFar = 7f; job.Step();
                Assert.AreEqual(0, job.Read<float>("InstanceFades"), "Absolute far must cap enlarged ratio range.");
                job.Parameters.AbsoluteFar = 0f; job.Parameters.AbsoluteNear = 12f;
                job.Put("CullThresholdsSq", 10000f); job.Set("LodBias", .5f); job.Step();
                Assert.AreEqual(0, job.Read<float>("InstanceFades"), "Absolute near must remain unscaled.");
            }
        }

        public sealed class Holder : ScriptableObject
        {
            public StaticInstanceGlobalSettings settings = new StaticInstanceGlobalSettings();
        }
        public sealed class SettingsWindow : EditorWindow
        {
            [NonSerialized] public SerializedObject data; public int draws;
            void OnGUI() { if (data == null) return; EditorGUILayout.PropertyField(data.FindProperty("settings"), true); draws++; }
        }

        [UnityTest]
        public IEnumerator ProjectValueRuntimeOverrideResetAndRepaintAreIndependent()
        {
            var fields = new Dictionary<FieldInfo, object>();
            foreach (var field in typeof(StaticInstanceLodSystem).GetFields(BindingFlags.Public | BindingFlags.Static))
                if (!field.IsLiteral && !field.IsInitOnly) fields.Add(field, field.GetValue(null));
            var holder = ScriptableObject.CreateInstance<Holder>();
            var window = ScriptableObject.CreateInstance<SettingsWindow>();
            try
            {
                Assert.AreEqual(1f, holder.settings.lodBias);
                holder.settings.lodBias = 1.5f; holder.settings.ApplyToSystem();
                Assert.AreEqual(1.5f, StaticInstanceLodSystem.LodBias);
                StaticInstanceLodSystem.LodBias = .5f;
                Assert.AreEqual(1.5f, holder.settings.lodBias);
                holder.settings.ApplyToSystem(); Assert.AreEqual(1.5f, StaticInstanceLodSystem.LodBias);
                var data = new SerializedObject(holder); window.data = data; window.ShowUtility();
                yield return null; window.Repaint(); yield return null;
                window.SendEvent(new Event { type = EventType.Layout }); window.SendEvent(new Event { type = EventType.Repaint });
                Assert.Greater(window.draws, 0); Assert.IsFalse(data.hasModifiedProperties);
                Assert.AreEqual(1.5f, holder.settings.lodBias);
                holder.settings.lodBias = -1f; Assert.AreEqual(0f, holder.settings.lodBias);
            }
            finally
            {
                window.Close(); UnityEngine.Object.DestroyImmediate(window); UnityEngine.Object.DestroyImmediate(holder);
                foreach (var pair in fields) pair.Key.SetValue(null, pair.Value);
            }
        }
    }
}
