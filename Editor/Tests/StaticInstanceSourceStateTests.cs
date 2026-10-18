using System.Reflection;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace StaticInstances.Editor.Tests
{
    public sealed class StaticInstanceSourceStateTests
    {
        private Scene testScene;
        private Material material;

        [SetUp]
        public void SetUp()
        {
            testScene = EditorSceneManager.NewPreviewScene();
            material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        }

        [TearDown]
        public void TearDown()
        {
            EditorSceneManager.ClosePreviewScene(testScene);
            Object.DestroyImmediate(material);
        }

        [Test]
        public void BakedInitialize_SuppressesOnlyEligibleSources_AndShutdownRestoresThem()
        {
            var root = new GameObject("owner");
            SceneManager.MoveGameObjectToScene(root, testScene);

            MeshRenderer enabledSource = AddRenderer(root.transform, "enabled", true, true, true);
            MeshRenderer disabledSource = AddRenderer(root.transform, "disabled", false, true, true);
            MeshRenderer inactiveSource = AddRenderer(root.transform, "inactive", true, true, true);
            inactiveSource.gameObject.SetActive(false);
            MeshRenderer missingFilterSource =
                AddRenderer(root.transform, "TextMesh sign", true, false, false);
            MeshRenderer nullMeshSource =
                AddRenderer(root.transform, "null mesh", true, true, false);
            var scatter = root.AddComponent<StaticInstanceScatter>();

            Assert.IsTrue(StaticInstanceBakeUtility.BakeGroup(scatter),
                "the enabled source should produce a bake");
            Assert.IsFalse(StaticInstanceBakeUtility.IsBakeStale(scatter),
                "Initialize must exercise the baked path rather than runtime collection");

            scatter.Initialize();

            Assert.IsTrue(scatter.IsInitialized);
            Assert.IsFalse(enabledSource.enabled);
            Assert.IsFalse(disabledSource.enabled);
            Assert.IsTrue(inactiveSource.enabled,
                "an inactive renderer excluded from the bake must not be changed");
            Assert.IsTrue(missingFilterSource.enabled,
                "a TextMesh-style renderer without a MeshFilter must remain visible");
            Assert.IsTrue(nullMeshSource.enabled,
                "a renderer with a null sharedMesh must not be suppressed");

            InvokeOnDisable(scatter);

            Assert.IsFalse(scatter.IsInitialized);
            Assert.IsTrue(enabledSource.enabled);
            Assert.IsFalse(disabledSource.enabled,
                "a renderer excluded from the bake because it was disabled must stay disabled");
            Assert.IsTrue(inactiveSource.enabled,
                "an inactive renderer's component state must survive the SIS lifecycle");
            Assert.IsTrue(missingFilterSource.enabled,
                "a renderer without a MeshFilter must keep its authored enabled state");
            Assert.IsTrue(nullMeshSource.enabled);
        }

        [Test]
        public void RepeatedLifecycle_SuppressesAndRestoresEligibleSource()
        {
            var root = new GameObject("owner");
            SceneManager.MoveGameObjectToScene(root, testScene);
            MeshRenderer source = AddRenderer(root.transform, "source", true, true, true);
            var scatter = root.AddComponent<StaticInstanceScatter>();
            Assert.IsTrue(StaticInstanceBakeUtility.BakeGroup(scatter));
            Assert.IsFalse(StaticInstanceBakeUtility.IsBakeStale(scatter));

            scatter.Initialize();
            InvokeOnDisable(scatter);
            Assert.IsTrue(source.enabled);

            scatter.Initialize();
            Assert.IsTrue(scatter.IsInitialized);
            Assert.IsFalse(source.enabled);
            InvokeOnDisable(scatter);

            Assert.IsTrue(source.enabled,
                "each lifecycle must restore the state captured for that lifecycle");
        }

        [Test]
        public void RuntimeCollection_SuppressesOnlyEligibleSources()
        {
            var root = new GameObject("unbaked owner");
            SceneManager.MoveGameObjectToScene(root, testScene);
            MeshRenderer eligible = AddRenderer(root.transform, "eligible", true, true, true);
            MeshRenderer disabled = AddRenderer(root.transform, "disabled", false, true, true);
            MeshRenderer missingFilter =
                AddRenderer(root.transform, "TextMesh sign", true, false, false);
            MeshRenderer nullMesh = AddRenderer(root.transform, "null mesh", true, true, false);
            var scatter = root.AddComponent<StaticInstanceScatter>();
            LogAssert.Expect(LogType.Warning,
                new System.Text.RegularExpressions.Regex("No usable baked data"));

            scatter.Initialize();

            Assert.IsFalse(eligible.enabled);
            Assert.IsFalse(disabled.enabled);
            Assert.IsTrue(missingFilter.enabled);
            Assert.IsTrue(nullMesh.enabled);

            InvokeOnDisable(scatter);
            Assert.IsTrue(eligible.enabled);
            Assert.IsFalse(disabled.enabled);
            Assert.IsTrue(missingFilter.enabled);
            Assert.IsTrue(nullMesh.enabled);
        }

        private MeshRenderer AddRenderer(
            Transform parent, string name, bool enabled, bool includeMeshFilter, bool assignMesh)
        {
            var node = new GameObject(name);
            node.transform.SetParent(parent, false);
            if (includeMeshFilter)
            {
                var filter = node.AddComponent<MeshFilter>();
                if (assignMesh)
                    filter.sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            }

            var renderer = node.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.enabled = enabled;
            return renderer;
        }

        private static void InvokeOnDisable(StaticInstanceScatter scatter)
        {
            MethodInfo callback = typeof(StaticInstanceScatter).GetMethod(
                "OnDisable", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(callback, "the lifecycle callback seam must exist");
            callback.Invoke(scatter, null);
        }
    }
}
