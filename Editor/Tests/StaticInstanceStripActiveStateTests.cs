using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StaticInstances.Editor.Tests
{
    // The strip flattens collider carriers out of render-only ancestors.
    // SetParent keeps the world pose but not an inherited inactive state, so
    // a self-active collider under an inactive render-only ancestor would come
    // back to life in builds.
    public sealed class StaticInstanceStripActiveStateTests
    {
        private Scene testScene;
        private StaticInstanceScatter owner;

        [SetUp]
        public void SetUp()
        {
            testScene = EditorSceneManager.NewPreviewScene();
            var root = new GameObject("owner");
            SceneManager.MoveGameObjectToScene(root, testScene);
            owner = root.AddComponent<StaticInstanceScatter>();
        }

        [TearDown]
        public void TearDown() => EditorSceneManager.ClosePreviewScene(testScene);

        [Test]
        public void ColliderUnderInactiveRenderOnlyAncestorStaysInactive()
        {
            GameObject wall = AddRenderOnly(owner.transform, "wall");
            wall.SetActive(false);
            GameObject collider = AddColliderCarrier(wall.transform, "wall_mesh");
            Assert.IsTrue(collider.activeSelf);
            Assert.IsFalse(collider.activeInHierarchy);

            StaticInstanceBakeUtility.StripBakedSources(owner);

            Assert.AreEqual(owner.transform, collider.transform.parent, "flattened to the owner");
            Assert.IsTrue(wall == null, "the render-only ancestor is deleted");
            Assert.IsFalse(collider.activeInHierarchy, "must not be re-enabled by the flatten");
        }

        [Test]
        public void ColliderUnderInactiveGrandparentStaysInactive()
        {
            GameObject outer = AddRenderOnly(owner.transform, "outer");
            outer.SetActive(false);
            GameObject inner = AddRenderOnly(outer.transform, "inner");
            GameObject collider = AddColliderCarrier(inner.transform, "mesh");

            StaticInstanceBakeUtility.StripBakedSources(owner);

            Assert.AreEqual(owner.transform, collider.transform.parent);
            Assert.IsFalse(collider.activeInHierarchy);
        }

        [Test]
        public void ColliderUnderActiveAncestorStaysActive()
        {
            GameObject wall = AddRenderOnly(owner.transform, "wall");
            GameObject collider = AddColliderCarrier(wall.transform, "wall_mesh");

            StaticInstanceBakeUtility.StripBakedSources(owner);

            Assert.AreEqual(owner.transform, collider.transform.parent);
            Assert.IsTrue(collider.activeSelf);
            Assert.IsTrue(collider.activeInHierarchy);
        }

        [Test]
        public void FlattenKeepsWorldPose()
        {
            GameObject wall = AddRenderOnly(owner.transform, "wall");
            wall.transform.SetLocalPositionAndRotation(new Vector3(1f, 2f, 3f), Quaternion.Euler(0f, 30f, 0f));
            wall.transform.localScale = Vector3.one * 2f;
            wall.SetActive(false);
            GameObject collider = AddColliderCarrier(wall.transform, "wall_mesh");
            collider.transform.localPosition = new Vector3(0.5f, 0f, 0f);
            Matrix4x4 before = collider.transform.localToWorldMatrix;

            StaticInstanceBakeUtility.StripBakedSources(owner);

            Matrix4x4 after = collider.transform.localToWorldMatrix;
            for (int i = 0; i < 16; i++)
                Assert.AreEqual(before[i], after[i], 1e-4f, $"matrix element {i}");
        }

        private static GameObject AddRenderOnly(Transform parent, string name)
        {
            var node = new GameObject(name);
            node.transform.SetParent(parent, false);
            node.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            node.AddComponent<MeshRenderer>();
            return node;
        }

        private static GameObject AddColliderCarrier(Transform parent, string name)
        {
            GameObject node = AddRenderOnly(parent, name);
            node.AddComponent<BoxCollider>();
            return node;
        }
    }
}
