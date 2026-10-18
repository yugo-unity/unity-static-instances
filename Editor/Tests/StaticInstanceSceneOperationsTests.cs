using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StaticInstances.Editor.Tests
{
    public sealed class StaticInstanceSceneOperationsTests
    {
        private Scene first;
        private Scene second;

        [SetUp]
        public void SetUp()
        {
            first = EditorSceneManager.NewPreviewScene();
            second = EditorSceneManager.NewPreviewScene();
        }

        [TearDown]
        public void TearDown()
        {
            EditorSceneManager.ClosePreviewScene(second);
            EditorSceneManager.ClosePreviewScene(first);
        }

        [Test]
        public void TargetDiscoveryIncludesInactiveOwnersButNeverOtherScenes()
        {
            var expected = AddOwner(first, "included");
            expected.gameObject.SetActive(false);
            AddOwner(second, "excluded");
            CollectionAssert.AreEqual(new[] { expected }, StaticInstanceSceneOperations.FindTargets(first));
        }

        [Test]
        public void BakeRejectsPreviewSceneWithoutChangingOwner()
        {
            var owner = AddOwner(first, "preview");
            string before = JsonUtility.ToJson(owner);
            var result = StaticInstanceSceneOperations.Bake(first);
            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(before, JsonUtility.ToJson(owner));
        }

        [Test]
        public void SaveDoesNotPromptForAnUntitledScene()
        {
            Assert.IsFalse(StaticInstanceSceneOperations.Save(first));
            Assert.IsFalse(StaticInstanceSceneOperations.Save(default));
        }

        [Test]
        public void CanceledSaveDoesNotStartTraversalOrChangeSceneSetup()
        {
            var before = EditorSceneManager.GetSceneManagerSetup();
            Assert.IsFalse(StaticInstanceSceneOperationSession.TryBegin(
                () => false, out var session, out var error));
            Assert.IsNull(session);
            Assert.IsNotEmpty(error);
            var after = EditorSceneManager.GetSceneManagerSetup();
            Assert.AreEqual(before.Length, after.Length);
            for (int i = 0; i < before.Length; i++)
            {
                Assert.AreEqual(before[i].path, after[i].path);
                Assert.AreEqual(before[i].isLoaded, after[i].isLoaded);
                Assert.AreEqual(before[i].isActive, after[i].isActive);
            }
        }

        [Test]
        public void DirtyUntitledSceneCannotBeDiscardedBySavePromptResult()
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            try
            {
                EditorSceneManager.MarkSceneDirty(scene);
                Assert.IsFalse(StaticInstanceSceneOperationSession.TryBegin(
                    () => true, out var session, out _));
                Assert.IsNull(session);
                Assert.IsTrue(scene.isLoaded);
                Assert.IsTrue(scene.isDirty);
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        [Test]
        public void FailedSaveAndRestoreKeepUnsavedSceneOpen()
        {
            var session = new StaticInstanceSceneOperationSession(EditorSceneManager.GetSceneManagerSetup());
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            try
            {
                var owner = AddOwner(scene, "preserved");
                EditorSceneManager.MarkSceneDirty(scene);
                Assert.IsFalse(StaticInstanceSceneOperations.Save(scene));
                Assert.IsFalse(session.TryRestore(out var error));
                Assert.IsNotEmpty(error);
                Assert.IsTrue(scene.isLoaded);
                Assert.IsTrue(scene.isDirty);
                Assert.IsNotNull(owner);
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        [Test]
        public void MovedOwnerRebakesItsSourcesAndUpdatesWorldMatrix()
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var owner = AddOwner(scene, "rebake");
            var source = GameObject.CreatePrimitive(PrimitiveType.Cube);
            source.transform.SetParent(owner.transform, false);
            Assert.IsTrue(StaticInstanceSceneOperations.Bake(scene).Succeeded);
            owner.transform.position = new Vector3(7f, 2f, 3f);
            Assert.IsTrue(StaticInstanceBakeUtility.Validate(owner, false).Exists(
                issue => issue.Severity == LogType.Error));
            var result = StaticInstanceSceneOperations.Bake(scene);
            Assert.IsTrue(result.Succeeded, result.Error);
            Assert.AreEqual(1, result.BakedTargets);
            Assert.AreEqual(owner.transform.localToWorldMatrix, owner.BakedOwnerMatrix);
            Assert.IsFalse(StaticInstanceBakeUtility.IsBakeStale(owner));
        }

        [Test]
        public void SourceAuthoringErrorStillBlocksRebakeAndPreservesOldData()
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var owner = AddOwner(scene, "invalid");
            var source = GameObject.CreatePrimitive(PrimitiveType.Cube);
            source.transform.SetParent(owner.transform, false);
            Assert.IsTrue(StaticInstanceSceneOperations.Bake(scene).Succeeded);
            source.GetComponent<MeshRenderer>().sharedMaterial = null;
            string before = JsonUtility.ToJson(owner);
            UnityEngine.TestTools.LogAssert.Expect(LogType.Error,
                new System.Text.RegularExpressions.Regex("has a null material"));
            Assert.IsFalse(StaticInstanceSceneOperations.Bake(scene).Succeeded);
            Assert.AreEqual(before, JsonUtility.ToJson(owner));
        }

        [Test]
        public void MissingSourcesCannotReplaceExistingBakeWithEmptyData()
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var owner = AddOwner(scene, "missing sources");
            var source = GameObject.CreatePrimitive(PrimitiveType.Cube);
            source.transform.SetParent(owner.transform, false);
            Assert.IsTrue(StaticInstanceSceneOperations.Bake(scene).Succeeded);
            Object.DestroyImmediate(source);
            owner.transform.position = Vector3.right;
            string before = JsonUtility.ToJson(owner);
            var result = StaticInstanceSceneOperations.Bake(scene);
            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(before, JsonUtility.ToJson(owner));
        }

        private static StaticInstanceScatter AddOwner(Scene scene, string name)
        {
            var node = new GameObject(name);
            SceneManager.MoveGameObjectToScene(node, scene);
            return node.AddComponent<StaticInstanceScatter>();
        }
    }
}
