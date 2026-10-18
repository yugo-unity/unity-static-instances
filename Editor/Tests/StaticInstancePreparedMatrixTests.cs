using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace StaticInstances.Editor.Tests
{
    public sealed class StaticInstancePreparedMatrixTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;

        private readonly List<GameObject> objects = new List<GameObject>();
        private readonly List<GraphicsBuffer> buffers = new List<GraphicsBuffer>();
        private readonly List<BatchID> batches = new List<BatchID>();
        private Scene previewScene;
        private bool previewSceneOpen;
        private string assetFolder;

        [SetUp]
        public void SetUp()
        {
            InvokeRendererStatic("ResetStatics");
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = batches.Count - 1; i >= 0; i--)
                RemoveBatch(batches[i]);
            for (int i = buffers.Count - 1; i >= 0; i--)
                buffers[i]?.Dispose();
            if (previewSceneOpen)
                EditorSceneManager.ClosePreviewScene(previewScene);
            for (int i = objects.Count - 1; i >= 0; i--)
                if (objects[i] != null)
                    UnityEngine.Object.DestroyImmediate(objects[i]);
            if (!string.IsNullOrEmpty(assetFolder) && AssetDatabase.IsValidFolder(assetFolder))
                AssetDatabase.DeleteAsset(assetFolder);
            InvokeRendererStatic("ResetStatics");
        }

        [Test]
        public void CreateStoresPackedCurrentThenUnityInverse()
        {
            Matrix4x4[] matrices = RepresentativeMatrices();

            StaticInstancePreparedMatrices prepared = StaticInstancePreparedMatrices.Create(matrices);

            Assert.IsNotNull(prepared);
            Assert.AreEqual(1, prepared.Format);
            Assert.AreEqual(1, StaticInstanceBakeUtility.BakeFormatVersion);
            Assert.AreEqual(matrices.Length, prepared.Count);
            Assert.AreEqual(24 * matrices.Length, prepared.Values.Length);
            Assert.AreEqual(Application.unityVersion, prepared.Producer);
            Assert.AreEqual(BitConverter.IsLittleEndian, prepared.LittleEndian);
            Assert.IsTrue(prepared.Validate());
            for (int i = 0; i < matrices.Length; i++)
            {
                AssertPackedBits(matrices[i], prepared.Values, 12 * i, $"current {i}");
                AssertPackedBits(matrices[i].inverse, prepared.Values,
                    12 * (matrices.Length + i), $"inverse {i}");
            }
        }

        [Test]
        public void DecodeRestoresArbitraryBottomRowsAndSignedZeroBits()
        {
            Matrix4x4 arbitrary = Matrix4x4.TRS(
                new Vector3(1.25f, -2.5f, 9f), Quaternion.Euler(17f, 39f, -11f),
                new Vector3(-2f, 0.5f, 3f));
            arbitrary.SetRow(3, new Vector4(7f, -8f, 0.125f, -3f));
            Matrix4x4 signedZero = Matrix4x4.identity;
            signedZero.m30 = IntBitsToFloat(unchecked((int)0x80000000));
            Matrix4x4 zero = Matrix4x4.zero;
            Matrix4x4[] source = { arbitrary, signedZero, zero };

            StaticInstancePreparedMatrices prepared = StaticInstancePreparedMatrices.Create(source);
            Assert.IsTrue(prepared.Validate());
            Matrix4x4[] decoded = prepared.Decode();

            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, prepared.BottomRowIndices);
            Assert.AreEqual(source.Length, decoded.Length);
            for (int i = 0; i < source.Length; i++)
                AssertMatrixBits(source[i], decoded[i], $"matrix {i}");
        }

        [Test]
        public void ValidateRejectsPayloadDigestCountIndicesAndVersionCorruption()
        {
            StaticInstancePreparedMatrices payload = StaticInstancePreparedMatrices.Create(
                RepresentativeMatrices());
            payload.Values[5] = IntBitsToFloat(FloatBits(payload.Values[5]) ^ 1);
            Assert.IsFalse(payload.Validate(), "same-shape payload mutation must invalidate the digest");

            payload = StaticInstancePreparedMatrices.Create(RepresentativeMatrices());
            payload.Count++;
            Assert.IsFalse(payload.Validate(), "count and 24*N payload length must agree");

            payload = StaticInstancePreparedMatrices.Create(new[] { Matrix4x4.zero, Matrix4x4.zero });
            payload.BottomRowIndices[1] = 0;
            RecomputeDigest(payload);
            Assert.IsFalse(payload.Validate(), "exception indices must be strictly increasing");

            payload = StaticInstancePreparedMatrices.Create(new[] { Matrix4x4.zero });
            payload.BottomRowIndices[0] = payload.Count;
            RecomputeDigest(payload);
            Assert.IsFalse(payload.Validate(), "exception indices must stay inside the instance range");

            payload = StaticInstancePreparedMatrices.Create(RepresentativeMatrices());
            payload.Format = 2;
            RecomputeDigest(payload);
            Assert.IsFalse(payload.Validate(), "unknown packed layouts must be refused");
        }

        [Test]
        public void ValidPayloadFromDifferentProducerIsDetectedWithoutCallingItCorrupt()
        {
            StaticInstancePreparedMatrices prepared = StaticInstancePreparedMatrices.Create(
                RepresentativeMatrices());
            prepared.Producer = Application.unityVersion + "-different";
            RecomputeDigest(prepared);

            Assert.IsTrue(prepared.Validate(), "producer identity is part of a valid serialized payload");
            Assert.IsFalse(prepared.MatchesProducer,
                "a valid payload from another producer must take the reconstruction fallback");
        }

        [Test]
        public void ApplyUsesOnlyPreparedStorageAndEachQueryReturnsAFreshExactDecode()
        {
            StaticInstanceScatter scatter = NewScatter("apply owner");
            Matrix4x4[] source = RepresentativeMatricesWithExceptionalRows();
            Matrix4x4[] expected = (Matrix4x4[])source.Clone();
            Bounds[] bounds = BoundsFor(source);
            var groups = new[] { new BakedDrawGroup { InstanceCount = source.Length } };
            int[] indices = SequentialIndices(source.Length);

            scatter.ApplyBakedData(source, bounds, groups, indices);
            source[0].m00 = 123456f;

            StaticInstancePreparedMatrices prepared =
                GetPrivate<StaticInstancePreparedMatrices>(scatter, "bakedPreparedMatrices");
            Assert.IsNotNull(prepared);
            Assert.AreEqual(24 * source.Length, prepared.Values.Length);
            Assert.IsTrue(scatter.GetBakedData(out Matrix4x4[] first, out Bounds[] actualBounds,
                out BakedDrawGroup[] actualGroups, out int[] actualIndices));
            Assert.IsTrue(scatter.GetBakedData(out Matrix4x4[] second, out _, out _, out _));
            Assert.AreNotSame(first, second, "explicit queries must not cache a decoded array");
            Assert.AreNotEqual(123456f, first[0].m00,
                "Apply must not retain the caller-owned Matrix4x4 array");
            for (int i = 0; i < first.Length; i++)
            {
                AssertMatrixBits(expected[i], first[i], $"applied matrix {i}");
                AssertMatrixBits(first[i], second[i], $"fresh decode {i}");
            }
            Assert.AreSame(bounds, actualBounds);
            Assert.AreSame(groups, actualGroups);
            Assert.AreSame(indices, actualIndices);
        }

        [Test]
        public void BakeFromHierarchyWritesPreparedStorage()
        {
            StaticInstanceScatter scatter = NewScatterWithRenderableSource();

            Assert.IsTrue(scatter.BakeFromHierarchy());

            StaticInstancePreparedMatrices prepared =
                GetPrivate<StaticInstancePreparedMatrices>(scatter, "bakedPreparedMatrices");
            Assert.IsNotNull(prepared);
            Assert.IsTrue(prepared.Validate());
            Assert.AreEqual(1, prepared.Count);
            Assert.IsTrue(scatter.GetBakedData(out Matrix4x4[] decoded, out _, out _, out _));
            AssertMatrixBits(scatter.transform.GetChild(0).localToWorldMatrix, decoded[0], "baked source");
        }

        [Test]
        public void PrefabSaveAndForcedReimportPreservePreparedFloatBits()
        {
            assetFolder = "Assets/SIPreparedMatrixTests_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(assetFolder));
            string path = assetFolder + "/Prepared.prefab";
            StaticInstanceScatter scatter = NewScatter("serialized owner");
            Matrix4x4[] source = RepresentativeMatricesWithExceptionalRows();
            scatter.ApplyBakedData(source, BoundsFor(source),
                new[] { new BakedDrawGroup { InstanceCount = source.Length } },
                SequentialIndices(source.Length));
            StaticInstancePreparedMatrices before =
                GetPrivate<StaticInstancePreparedMatrices>(scatter, "bakedPreparedMatrices");
            int[] expectedValueBits = FloatBits(before.Values);
            int[] expectedBottomBits = VectorBits(before.BottomRows);

            PrefabUtility.SaveAsPrefabAsset(scatter.gameObject, path);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path,
                ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            StaticInstanceScatter loaded = prefab.GetComponent<StaticInstanceScatter>();
            StaticInstancePreparedMatrices after =
                GetPrivate<StaticInstancePreparedMatrices>(loaded, "bakedPreparedMatrices");

            Assert.IsNotNull(after);
            Assert.IsTrue(after.Validate());
            CollectionAssert.AreEqual(expectedValueBits, FloatBits(after.Values));
            CollectionAssert.AreEqual(expectedBottomBits, VectorBits(after.BottomRows));
            Assert.IsTrue(loaded.GetBakedData(out Matrix4x4[] decoded, out _, out _, out _));
            for (int i = 0; i < source.Length; i++)
                AssertMatrixBits(source[i], decoded[i], $"serialized matrix {i}");
        }

        [Test]
        public void CorruptPreparedStorageIsRefused()
        {
            StaticInstanceScatter scatter = NewScatter("corrupt owner");
            Matrix4x4[] source = RepresentativeMatrices();
            scatter.ApplyBakedData(source, BoundsFor(source), Array.Empty<BakedDrawGroup>(),
                SequentialIndices(source.Length));
            StaticInstancePreparedMatrices prepared =
                GetPrivate<StaticInstancePreparedMatrices>(scatter, "bakedPreparedMatrices");
            prepared.Values[0] = IntBitsToFloat(FloatBits(prepared.Values[0]) ^ 1);

            Assert.IsFalse(scatter.ValidateBakedMatrices());
            Assert.IsFalse(scatter.GetBakedData(out Matrix4x4[] actual, out _, out _, out _));
            Assert.IsNull(actual, "a corrupt prepared payload must not decode");
        }

        [Test]
        public void ZeroCountCorruptPayloadRemainsVisibleToBuildPreflight()
        {
            StaticInstanceScatter scatter = NewScatter("source-less corrupt owner");
            Matrix4x4[] source = RepresentativeMatrices();
            scatter.ApplyBakedData(source, BoundsFor(source), Array.Empty<BakedDrawGroup>(),
                SequentialIndices(source.Length));
            StaticInstancePreparedMatrices prepared =
                GetPrivate<StaticInstancePreparedMatrices>(scatter, "bakedPreparedMatrices");
            prepared.Count = 0;

            Assert.IsFalse(scatter.HasBakedData,
                "a zero count is not a structurally valid prepared bake");
            Assert.IsTrue(scatter.HasStoredBakedMatrices,
                "remaining serialized payload must still be visible to fail-closed build checks");
            Assert.IsFalse(scatter.ValidateBakedMatrices());

            var violations = new List<string>();
            StaticInstanceBuildScan.CollectTargetViolations(
                scatter.gameObject.scene, scatter, violations);

            Assert.IsTrue(violations.Exists(message => message.Contains("invalid matrix bake")),
                "a source-less corrupt prepared payload must be refused before a bundled build");
        }

        [Test]
        public void StrippedCorruptPayloadDoesNotRegisterFromSurvivingPartialSources()
        {
            StaticInstanceScatter scatter = NewScatterWithRenderableSource();
            Matrix4x4[] source = RepresentativeMatrices();
            scatter.ApplyBakedData(source, BoundsFor(source), Array.Empty<BakedDrawGroup>(),
                SequentialIndices(source.Length));
            StaticInstancePreparedMatrices prepared =
                GetPrivate<StaticInstancePreparedMatrices>(scatter, "bakedPreparedMatrices");
            prepared.Count = 0;
            SetPrivate(scatter, "bakedSourcesStripped", true);

            LogAssert.Expect(LogType.Warning,
                new System.Text.RegularExpressions.Regex("prepared matrix data is invalid"));
            LogAssert.Expect(LogType.Error,
                new System.Text.RegularExpressions.Regex("sources were stripped"));

            scatter.Initialize();

            Assert.IsFalse(scatter.IsInitialized,
                "a surviving subset of stripped sources cannot reconstruct the complete owner");
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ZeroLayoutAndCountWithSurvivingPayloadOrDigestIsRejected(bool keepValues)
        {
            StaticInstanceScatter scatter = NewScatter(
                keepValues ? "leftover values owner" : "leftover digest owner");
            var prepared = new StaticInstancePreparedMatrices();
            if (keepValues)
            {
                prepared.Values = new[] { 1f };
            }
            else
            {
                var digest = new Hash128();
                digest.Append(1);
                prepared.Digest = digest;
            }
            SetPrivate(scatter, "bakedPreparedMatrices", prepared);

            Assert.IsFalse(scatter.HasBakedData);
            Assert.IsTrue(scatter.HasStoredBakedMatrices,
                "surviving serialized prepared state must still count as stored matrices");
            Assert.IsFalse(scatter.ValidateBakedMatrices());
        }

        [Test]
        public void GroupsAccessorDoesNotRequireAValidOrDecodableMatrixPayload()
        {
            StaticInstanceScatter scatter = NewScatter("groups owner");
            var groups = new[] { new BakedDrawGroup { InstanceCount = 17 } };
            scatter.ApplyBakedData(RepresentativeMatrices(), BoundsFor(RepresentativeMatrices()),
                groups, SequentialIndices(RepresentativeMatrices().Length));
            StaticInstancePreparedMatrices prepared =
                GetPrivate<StaticInstancePreparedMatrices>(scatter, "bakedPreparedMatrices");
            prepared.Count = int.MaxValue;
            prepared.Values = null;

            Assert.AreSame(groups, scatter.GetBakedDrawGroups());
        }

        [Test]
        public void ClearResetsPreparedAndAllPublicBakeState()
        {
            StaticInstanceScatter scatter = NewScatter("clear owner");
            Matrix4x4[] source = RepresentativeMatrices();
            scatter.ApplyBakedData(source, BoundsFor(source), Array.Empty<BakedDrawGroup>(),
                SequentialIndices(source.Length));
            scatter.ApplyBakedSourceHash("source hash");

            scatter.ClearBakedData();

            Assert.IsFalse(scatter.HasBakedData);
            Assert.AreEqual(0, scatter.BakedInstanceCount);
            Assert.IsNull(GetPrivate<StaticInstancePreparedMatrices>(scatter, "bakedPreparedMatrices"));
            Assert.IsNull(scatter.GetBakedDrawGroups());
            Assert.IsNull(scatter.BakedSourceHash);
            Assert.AreEqual(default(Matrix4x4), scatter.BakedOwnerMatrix);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PreparedAndCurrentRoutesUploadIdenticalGpuMatrixBytes(bool motionVectors)
        {
            Matrix4x4[] source = RepresentativeMatrices();
            StaticInstancePreparedMatrices prepared = StaticInstancePreparedMatrices.Create(source);

            BatchID rawBatch = StaticInstanceBuilder.CreateMatrixBatch(
                source, motionVectors, out GraphicsBuffer rawBuffer);
            Track(rawBatch, rawBuffer);
            BatchID preparedBatch = StaticInstanceBuilder.CreateMatrixBatch(
                null, prepared, motionVectors, out GraphicsBuffer preparedBuffer);
            Track(preparedBatch, preparedBuffer);

            int floatBase = StaticInstanceBuilder.MatricesByteBase / sizeof(float);
            float[] raw = ReadFloats(rawBuffer, floatBase, prepared.Values.Length);
            float[] direct = ReadFloats(preparedBuffer, floatBase, prepared.Values.Length);
            AssertFloatBits(prepared.Values, direct, "prepared upload");
            AssertFloatBits(raw, direct, "raw and prepared GPU routes");
        }

        private StaticInstanceScatter NewScatter(string name)
        {
            var go = new GameObject(name);
            objects.Add(go);
            return go.AddComponent<StaticInstanceScatter>();
        }

        private StaticInstanceScatter NewScatterWithRenderableSource()
        {
            previewScene = EditorSceneManager.NewPreviewScene();
            previewSceneOpen = true;
            var root = new GameObject("bake owner");
            SceneManager.MoveGameObjectToScene(root, previewScene);
            var child = GameObject.CreatePrimitive(PrimitiveType.Cube);
            SceneManager.MoveGameObjectToScene(child, previewScene);
            child.transform.SetParent(root.transform, false);
            child.transform.localPosition = new Vector3(2.25f, -3.5f, 4.75f);
            child.transform.localRotation = Quaternion.Euler(13f, -27f, 41f);
            child.transform.localScale = new Vector3(-2f, 0.375f, 5f);
            objects.Add(root);
            return root.AddComponent<StaticInstanceScatter>();
        }

        private static Matrix4x4[] RepresentativeMatrices()
        {
            Matrix4x4 sheared = Matrix4x4.identity;
            sheared.m00 = 1.125f;
            sheared.m10 = -2.25f;
            sheared.m20 = 3.5f;
            sheared.m01 = 0.0625f;
            sheared.m11 = -0.75f;
            sheared.m21 = 1.875f;
            sheared.m02 = 0.5f;
            sheared.m12 = 2.125f;
            sheared.m22 = -1.5f;
            sheared.m03 = 123.25f;
            sheared.m13 = -0.03125f;
            sheared.m23 = 17.75f;
            return new[]
            {
                Matrix4x4.identity,
                Matrix4x4.TRS(new Vector3(-7.25f, 11.5f, 0.125f),
                    Quaternion.Euler(23f, -117f, 9f), new Vector3(-2f, 0.375f, 4.5f)),
                sheared,
            };
        }

        private static Matrix4x4[] RepresentativeMatricesWithExceptionalRows()
        {
            Matrix4x4[] matrices = RepresentativeMatrices();
            matrices[0].m30 = IntBitsToFloat(unchecked((int)0x80000000));
            matrices[1].SetRow(3, new Vector4(0.25f, -2f, 9f, -0.5f));
            return matrices;
        }

        private static Bounds[] BoundsFor(Matrix4x4[] matrices)
        {
            var result = new Bounds[matrices.Length];
            for (int i = 0; i < result.Length; i++)
                result[i] = new Bounds(matrices[i].GetColumn(3), Vector3.one * (i + 1));
            return result;
        }

        private static int[] SequentialIndices(int count)
        {
            var result = new int[count];
            for (int i = 0; i < count; i++)
                result[i] = i;
            return result;
        }

        private static void AssertPackedBits(
            Matrix4x4 expected, float[] actual, int offset, string context)
        {
            for (int column = 0; column < 4; column++)
                for (int row = 0; row < 3; row++)
                    Assert.AreEqual(FloatBits(expected[row, column]), FloatBits(actual[offset++]),
                        $"{context} [{row},{column}]");
        }

        private static void AssertMatrixBits(Matrix4x4 expected, Matrix4x4 actual, string context)
        {
            for (int column = 0; column < 4; column++)
                for (int row = 0; row < 4; row++)
                    Assert.AreEqual(FloatBits(expected[row, column]), FloatBits(actual[row, column]),
                        $"{context} [{row},{column}]");
        }

        private static void AssertFloatBits(float[] expected, float[] actual, string context)
        {
            Assert.AreEqual(expected.Length, actual.Length, context);
            for (int i = 0; i < expected.Length; i++)
                Assert.AreEqual(FloatBits(expected[i]), FloatBits(actual[i]), $"{context} float {i}");
        }

        private static int[] FloatBits(float[] values)
        {
            var result = new int[values.Length];
            for (int i = 0; i < values.Length; i++)
                result[i] = FloatBits(values[i]);
            return result;
        }

        private static int[] VectorBits(Vector4[] values)
        {
            var result = new int[values.Length * 4];
            for (int i = 0; i < values.Length; i++)
                for (int c = 0; c < 4; c++)
                    result[4 * i + c] = FloatBits(values[i][c]);
            return result;
        }

        private static int FloatBits(float value) => BitConverter.SingleToInt32Bits(value);
        private static float IntBitsToFloat(int value) => BitConverter.Int32BitsToSingle(value);

        private static void RecomputeDigest(StaticInstancePreparedMatrices prepared)
        {
            MethodInfo method = typeof(StaticInstancePreparedMatrices).GetMethod(
                "ComputeDigest", PrivateInstance);
            Assert.IsNotNull(method);
            prepared.Digest = (Hash128)method.Invoke(prepared, null);
        }

        private static T GetPrivate<T>(object target, string fieldName)
        {
            FieldInfo field = target.GetType().GetField(fieldName, PrivateInstance);
            Assert.IsNotNull(field, fieldName);
            return (T)field.GetValue(target);
        }

        private static void SetPrivate(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, PrivateInstance);
            Assert.IsNotNull(field, fieldName);
            field.SetValue(target, value);
        }

        private static float[] ReadFloats(GraphicsBuffer buffer, int sourceStart, int count)
        {
            var result = new float[count];
            buffer.GetData(result, 0, sourceStart, count);
            return result;
        }

        private void Track(BatchID batch, GraphicsBuffer buffer)
        {
            batches.Add(batch);
            buffers.Add(buffer);
        }

        private static void RemoveBatch(BatchID batch)
        {
            MethodInfo method = typeof(StaticInstanceRendererSystem).GetMethod(
                "RemoveBatch", PrivateStatic);
            Assert.IsNotNull(method);
            method.Invoke(null, new object[] { batch });
        }

        private static void InvokeRendererStatic(string methodName)
        {
            MethodInfo method = typeof(StaticInstanceRendererSystem).GetMethod(methodName, PrivateStatic);
            Assert.IsNotNull(method, methodName);
            method.Invoke(null, null);
        }
    }
}
