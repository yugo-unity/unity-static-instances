using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Rendering;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StaticInstances.Editor.Tests
{
    public sealed class StaticInstanceShaderStripperTests
    {
        private BatchRendererGroupStrippingMode savedStrippingMode;
        private Scene testScene;
        private bool testSceneOpen;

        [SetUp]
        public void SetUp()
        {
            savedStrippingMode = EditorGraphicsSettings.batchRendererGroupShaderStrippingMode;
            StaticInstanceShaderStripping.Reset();
            testScene = EditorSceneManager.NewPreviewScene();
            testSceneOpen = true;
        }

        [TearDown]
        public void TearDown()
        {
            SetStrippingMode(savedStrippingMode);
            StaticInstanceShaderStripping.Reset();
            if (testSceneOpen)
                EditorSceneManager.ClosePreviewScene(testScene);
        }

        [Test]
        public void SceneCollection_CollectsScatterMaterials()
        {
            SetStrippingMode(BatchRendererGroupStrippingMode.KeepAll);
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            Assert.IsNotNull(shader, "URP/Lit is required by this test assembly.");
            CreateScatterOwner(shader);

            StaticInstanceShaderStripping.Active = true;
            StaticInstanceSceneShaderCollector.CollectScene(testScene, canFailBuild: true);

            Assert.IsTrue(StaticInstanceShaderStripping.KeptShaders.Contains(shader));
        }

        [Test]
        public void SceneCollection_RequiresKeepAllForScatterContent()
        {
            SetStrippingMode(BatchRendererGroupStrippingMode.StripAll);
            CreateScatterOwner(Shader.Find("Universal Render Pipeline/Lit"));
            StaticInstanceShaderStripping.Active = true;

            Assert.Throws<BuildFailedException>(
                () => StaticInstanceSceneShaderCollector.CollectScene(testScene, canFailBuild: true));
        }

        [Test]
        public void SceneCollection_AllowsASceneWithoutStaticInstances()
        {
            SetStrippingMode(BatchRendererGroupStrippingMode.StripAll);
            var plain = new GameObject("plain");
            SceneManager.MoveGameObjectToScene(plain, testScene);
            StaticInstanceShaderStripping.Active = true;

            Assert.DoesNotThrow(
                () => StaticInstanceSceneShaderCollector.CollectScene(testScene, canFailBuild: true));
        }

        private void CreateScatterOwner(Shader shader)
        {
            Assert.IsNotNull(shader);
            var owner = new GameObject("scatter");
            SceneManager.MoveGameObjectToScene(owner, testScene);
            owner.AddComponent<StaticInstanceScatter>();
            var child = new GameObject("renderer");
            child.transform.SetParent(owner.transform, false);
            child.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            child.AddComponent<MeshRenderer>().sharedMaterial = new Material(shader);
        }

        private static void SetStrippingMode(BatchRendererGroupStrippingMode mode)
        {
            var settings = new SerializedObject(
                AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset")[0]);
            settings.FindProperty("m_BrgStripping").enumValueIndex = (int)mode;
            settings.ApplyModifiedPropertiesWithoutUndo();
            Assert.AreEqual(mode, EditorGraphicsSettings.batchRendererGroupShaderStrippingMode);
        }
    }
}
