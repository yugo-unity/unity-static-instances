using System;
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace StaticInstances.Editor.Tests
{
    public sealed class StaticInstanceSceneBatchTests
    {
        private string folder;

        [SetUp]
        public void SetUp()
        {
            string name = "SI_BatchTest_" + Guid.NewGuid().ToString("N");
            folder = "Assets/" + name;
            AssetDatabase.CreateFolder("Assets", name);
        }

        [TearDown]
        public void TearDown()
        {
            StaticInstanceSceneBatch.SaveScene = StaticInstanceSceneOperations.Save;
            StaticInstanceSceneBatch.Cancel();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AssetDatabase.DeleteAsset(folder);
        }

        [UnityTest]
        public IEnumerator TwoScenesAreBakedSavedAndOriginalSetupRestored()
        {
            string a = CreateScene("A");
            string b = CreateScene("B");
            EditorSceneManager.OpenScene(a, OpenSceneMode.Single);
            Assert.IsTrue(StaticInstanceSceneBatch.Start(new[] { a, b }, saveResults: true, prompt: false));
            for (int i = 0; i < 300 && StaticInstanceSceneBatch.IsRunning; i++)
                yield return null;
            Assert.IsFalse(StaticInstanceSceneBatch.IsRunning, StaticInstanceSceneBatch.Status);
            StringAssert.Contains("Completed and saved 2", StaticInstanceSceneBatch.Status);
            Assert.AreEqual(a, SceneManager.GetActiveScene().path);
            Assert.AreEqual(1, SceneManager.sceneCount);
            Assert.IsTrue(StaticInstanceSceneOperations.FindTargets(SceneManager.GetActiveScene()).Single().HasBakedData);
            EditorSceneManager.OpenScene(b, OpenSceneMode.Single);
            Assert.IsTrue(StaticInstanceSceneOperations.FindTargets(SceneManager.GetActiveScene()).Single().HasBakedData);
        }

        [UnityTest]
        public IEnumerator CancelBeforeFirstSceneDoesNotBakeOrReplaceSetup()
        {
            string a = CreateScene("A");
            string b = CreateScene("B");
            EditorSceneManager.OpenScene(a, OpenSceneMode.Single);
            Assert.IsTrue(StaticInstanceSceneBatch.Start(new[] { b }, true, prompt: false));
            StaticInstanceSceneBatch.Cancel();
            for (int i = 0; i < 100 && StaticInstanceSceneBatch.IsRunning; i++)
                yield return null;
            Assert.IsFalse(StaticInstanceSceneBatch.IsRunning);
            Assert.AreEqual(a, SceneManager.GetActiveScene().path);
            EditorSceneManager.OpenScene(b, OpenSceneMode.Single);
            Assert.IsFalse(StaticInstanceSceneOperations.FindTargets(SceneManager.GetActiveScene()).Single().HasBakedData);
        }

        [Test]
        public void CancelBeforeTheFirstTickRestoresTheOriginalScene()
        {
            string a = CreateScene("A");
            string b = CreateScene("B");
            EditorSceneManager.OpenScene(a, OpenSceneMode.Single);
            Assert.IsTrue(StaticInstanceSceneBatch.Start(new[] { b }, true, prompt: false));
            StaticInstanceSceneBatch.Cancel();
            Assert.IsFalse(StaticInstanceSceneBatch.IsRunning);
            StringAssert.StartsWith("Canceled", StaticInstanceSceneBatch.Status);
            Assert.AreEqual(a, SceneManager.GetActiveScene().path);
        }

        [UnityTest]
        public IEnumerator SaveFailureKeepsTheEditedSceneOpenAndDoesNotWriteItsBake()
        {
            string a = CreateScene("A");
            string b = CreateScene("B");
            EditorSceneManager.OpenScene(a, OpenSceneMode.Single);
            StaticInstanceSceneBatch.SaveScene = _ => false;
            Assert.IsTrue(StaticInstanceSceneBatch.Start(new[] { b }, true, prompt: false));
            for (int i = 0; i < 300 && StaticInstanceSceneBatch.IsRunning; i++)
                yield return null;
            Assert.IsFalse(StaticInstanceSceneBatch.IsRunning);
            StringAssert.Contains("Could not save", StaticInstanceSceneBatch.Status);
            Assert.AreEqual(b, SceneManager.GetActiveScene().path);
            Assert.IsTrue(SceneManager.GetActiveScene().isDirty);
            Assert.IsTrue(StaticInstanceSceneOperations.FindTargets(SceneManager.GetActiveScene()).Single().HasBakedData);
            EditorSceneManager.OpenScene(b, OpenSceneMode.Single);
            Assert.IsFalse(StaticInstanceSceneOperations.FindTargets(SceneManager.GetActiveScene()).Single().HasBakedData);
        }

        private string CreateScene(string name)
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var owner = new GameObject(name).AddComponent<StaticInstanceScatter>();
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.transform.SetParent(owner.transform, false);
            string path = folder + "/" + name + ".unity";
            Assert.IsTrue(EditorSceneManager.SaveScene(scene, path));
            return path;
        }
    }
}
