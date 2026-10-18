using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace StaticInstances.Editor.Tests
{
    public sealed class StaticInstanceMotionVectorTests
    {
        const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
        readonly List<StaticInstanceOwner> owners = new List<StaticInstanceOwner>();
        readonly List<GraphicsBuffer> buffers = new List<GraphicsBuffer>();
        readonly List<Mesh> meshes = new List<Mesh>();
        readonly List<Material> materials = new List<Material>();
        Mesh mesh; Material material;
        [SetUp] public void Setup()
        {
            mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };
            mesh.RecalculateBounds();
            material = new Material(Shader.Find("Universal Render Pipeline/Lit")) { enableInstancing = true };
        }
        [TearDown] public void Cleanup()
        {
            foreach (var value in meshes) StaticInstanceRendererSystem.UnregisterMesh(value);
            foreach (var value in materials) StaticInstanceRendererSystem.UnregisterMaterial(value);
            foreach (var value in owners) { StaticInstanceRendererSystem.UnregisterOwner(value); var owner = value; StaticInstanceBuilder.DisposeOwner(ref owner); }
            foreach (var value in buffers) value.Dispose();
            owners.Clear(); buffers.Clear(); meshes.Clear(); materials.Clear();
            UnityEngine.Object.DestroyImmediate(mesh); UnityEngine.Object.DestroyImmediate(material);
        }
        // `staticClone[i]` gives group i a wind-stripped StaticMaterial, which
        // has no vertex animation and therefore raises no motion vectors.
        StaticInstanceOwner Build(bool enabled, params bool[] staticClone)
        {
            int count = staticClone.Length;
            var groups = new BakedDrawGroup[count]; var matrices = new Matrix4x4[count]; var bounds = new Bounds[count]; var indices = new int[count];
            for (int i = 0; i < count; i++)
            {
                groups[i] = new BakedDrawGroup { Mesh = mesh, Material = material, InstanceCount = 1, InstanceOffset = i, StaticMaterial = staticClone[i] ? material : null, RenderingLayerMask = 1, FlipWinding = i % 2 == 1 };
                matrices[i] = Matrix4x4.TRS(new Vector3(i + 2, 3, 4), Quaternion.Euler(12, 34, 5), new Vector3(-2, .5f, 3)); bounds[i] = new Bounds(new Vector3(i + 2, 3, 4), Vector3.one); indices[i] = i;
            }
            var data = new CollectedData { Matrices = matrices, Bounds = bounds, DrawGroups = groups, InstanceIndices = indices };
            var owner = StaticInstanceBuilder.BuildRuntimeData(data, meshes, materials, out var buffer, out _, out _, out _, out _, motionVectors: enabled);
            owners.Add(owner); buffers.Add(buffer); StaticInstanceRendererSystem.RegisterOwner(owner); return owner;
        }
        static bool Motion(CullingDrawGroup group) => (group.Flags & CullingDrawGroup.MotionVectorsFlag) != 0;
        [TestCase(false)] [TestCase(true)]
        public void BuilderExcludesStaticClonesFromTheOptIn(bool enabled)
        {
            var owner = Build(enabled, false, true, true, false);
            Assert.AreEqual(enabled, owner.MotionVectors);
            for (int i = 0; i < 4; i++)
            {
                Assert.AreEqual(enabled && (i == 0 || i == 3), Motion(owner.DrawGroups[i]));
                Assert.AreEqual(i % 2 == 1, (owner.DrawGroups[i].Flags & CullingDrawGroup.FlipWindingFlag) != 0);
            }
            Assert.AreEqual(StaticInstanceBuilder.TotalBufferBytes(4), owner.InstanceData.count * owner.InstanceData.stride);
        }
        [Test] public void CombinedRangesAndBothCommandPathsPreserveMixedEligibility()
        {
            Build(true, false, true, true, false); Build(false, false); Build(true, false, false);
            typeof(StaticInstanceRendererSystem).GetMethod("RebuildCombinedData", PrivateStatic).Invoke(null, null);
            var groups = (NativeArray<CullingDrawGroup>)typeof(StaticInstanceRendererSystem).GetField("combinedDrawGroups", PrivateStatic).GetValue(null);
            var starts = (NativeArray<int>)typeof(StaticInstanceRendererSystem).GetField("segmentGroupStarts", PrivateStatic).GetValue(null);
            var counts = (NativeArray<int>)typeof(StaticInstanceRendererSystem).GetField("segmentGroupCounts", PrivateStatic).GetValue(null);
            var filters = (NativeArray<BatchFilterSettings>)typeof(StaticInstanceRendererSystem).GetField("segmentFilterSettings", PrivateStatic).GetValue(null);
            bool[] expected = { true, false, false, true, false, true, true };
            for (int i = 0; i < groups.Length; i++)
            {
                Assert.AreEqual(expected[i], Motion(groups[i]), "combined group " + i);
                var fast = (BatchDrawCommandFlags)typeof(StaticInstanceCullingBurst).GetMethod("FastPathCommandFlags", PrivateStatic).Invoke(null, new object[] { groups[i] });
                Assert.AreEqual(expected[i], (fast & BatchDrawCommandFlags.HasMotion) != 0);
                foreach (var type in typeof(StaticInstanceRendererSystem).GetNestedTypes(BindingFlags.NonPublic))
                {
                    var method = type.GetMethod("CommandFlags", PrivateStatic); if (method == null) continue;
                    var full = (BatchDrawCommandFlags)method.Invoke(null, new object[] { groups[i], true });
                    Assert.AreEqual(expected[i], (full & BatchDrawCommandFlags.HasMotion) != 0); Assert.IsTrue((full & BatchDrawCommandFlags.LODCrossFade) != 0);
                }
            }
            Assert.AreEqual(5, starts.Length);
            for (int s = 0; s < starts.Length; s++) for (int i = starts[s]; i < starts[s] + counts[s]; i++)
                Assert.AreEqual(expected[i] ? MotionVectorGenerationMode.Object : MotionVectorGenerationMode.Camera, filters[s].motionMode);
        }
    }
}
