using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StaticInstances.Editor.Tests
{
    public sealed class StaticInstanceOwnershipTests
    {
        private Scene testScene;

        [SetUp]
        public void SetUp() => testScene = EditorSceneManager.NewPreviewScene();

        [TearDown]
        public void TearDown() => EditorSceneManager.ClosePreviewScene(testScene);

        [Test]
        public void NestedScatterIsReportedAsUnsupported()
        {
            var parent = new GameObject("parent");
            SceneManager.MoveGameObjectToScene(parent, testScene);
            var outer = parent.AddComponent<StaticInstanceScatter>();
            var child = new GameObject("child");
            child.transform.SetParent(parent.transform, false);
            var nested = child.AddComponent<StaticInstanceScatter>();

            Assert.AreEqual(nested, StaticInstanceOwnership.FindUnsupportedNesting(outer));
            var issues = StaticInstanceBakeUtility.Validate(outer, includeStaleCheck: false);
            Assert.IsTrue(issues.Exists(issue => issue.Message.Contains("Nested SIS owners are not supported")));
        }

        [Test]
        public void OuterScatterDoesNotClaimNestedScattersRenderers()
        {
            var parent = new GameObject("parent");
            SceneManager.MoveGameObjectToScene(parent, testScene);
            var outer = parent.AddComponent<StaticInstanceScatter>();
            var outerRenderer = AddRenderer(parent.transform);
            var child = new GameObject("child");
            child.transform.SetParent(parent.transform, false);
            child.AddComponent<StaticInstanceScatter>();
            AddRenderer(child.transform);

            var owned = StaticInstanceOwnership.OwnedComponents<MeshRenderer>(outer);
            CollectionAssert.AreEquivalent(new[] { outerRenderer }, owned);
        }

        private static MeshRenderer AddRenderer(Transform parent)
        {
            var node = new GameObject("renderer");
            node.transform.SetParent(parent, false);
            node.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            return node.AddComponent<MeshRenderer>();
        }
    }
}
