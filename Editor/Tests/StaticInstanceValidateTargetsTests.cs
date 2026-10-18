using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StaticInstances.Editor.Tests
{
    // Count-only findings must carry the objects they count, so the Static
    // Instances window can list them.
    public sealed class StaticInstanceValidateTargetsTests
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

        // Only a non-uniform ancestor over a rotated child shears; the two
        // look-alikes beside it keep perpendicular axes.
        [Test]
        public void ShearListsOnlyTheRotatedChildOfANonUniformParent()
        {
            AddRenderer("stretched", new Vector3(1f, 2f, 1f), Quaternion.Euler(0f, 0f, 45f));
            AddRenderer("aligned", Vector3.one, Quaternion.Euler(0f, 0f, 90f), new Vector3(2f, 1f, 1f));
            MeshRenderer sheared = AddRenderer(
                "sheared", Vector3.one, Quaternion.Euler(0f, 0f, 45f), new Vector3(2f, 1f, 1f));

            CollectionAssert.AreEqual(new Object[] { sheared }, Find("sheared transform").Targets);
        }

        // Non-uniform scale alone is fully supported and must stay silent.
        [Test]
        public void NonUniformScaleAloneIsNotReported()
        {
            AddRenderer("stretched", new Vector3(1f, 2f, 1f), Quaternion.Euler(0f, 0f, 45f));

            var issues = StaticInstanceBakeUtility.Validate(owner, includeStaleCheck: false);
            Assert.IsFalse(
                issues.Exists(issue => issue.Message.Contains("shear") || issue.Message.Contains("non-uniform")),
                "a rotated, non-uniformly scaled renderer has perpendicular axes and nothing to report");
        }

        [Test]
        public void ZeroScaleIsNotShear()
        {
            Assert.IsFalse(StaticInstanceBakeUtility.HasShear(Matrix4x4.Scale(new Vector3(1f, 0f, 1f))));
        }

        [Test]
        public void NegativeScaleListsTheMirroredRenderer()
        {
            AddRenderer("plain", Vector3.one);
            MeshRenderer mirrored = AddRenderer("mirrored", new Vector3(-1f, 1f, 1f));

            CollectionAssert.AreEqual(new Object[] { mirrored }, Find("negative scale").Targets);
        }

        [Test]
        public void SkippedListsDisabledRenderer()
        {
            AddRenderer("drawn", Vector3.one);
            MeshRenderer disabled = AddRenderer("disabled", Vector3.one);
            disabled.enabled = false;

            CollectionAssert.AreEqual(new Object[] { disabled }, Find("were skipped").Targets);
        }

        [Test]
        public void MessageOnlyIssueHasEmptyTargets()
        {
            var issue = new StaticInstanceBakeUtility.Issue(LogType.Warning, "message only");
            Assert.IsNotNull(issue.Targets);
            Assert.AreEqual(0, issue.Targets.Length);
        }

        private StaticInstanceBakeUtility.Issue Find(string fragment)
        {
            var issues = StaticInstanceBakeUtility.Validate(owner, includeStaleCheck: false);
            int index = issues.FindIndex(issue => issue.Message.Contains(fragment));
            Assert.GreaterOrEqual(index, 0, $"no issue containing '{fragment}'");
            return issues[index];
        }

        private MeshRenderer AddRenderer(string name, Vector3 scale)
            => AddRenderer(name, scale, Quaternion.identity);

        // A non-null parentScale inserts an intermediate parent with that
        // scale between the owner and the renderer.
        private MeshRenderer AddRenderer(
            string name, Vector3 scale, Quaternion rotation, Vector3? parentScale = null)
        {
            Transform parent = owner.transform;
            if (parentScale.HasValue)
            {
                var intermediate = new GameObject(name + "_parent");
                intermediate.transform.SetParent(owner.transform, false);
                intermediate.transform.localScale = parentScale.Value;
                parent = intermediate.transform;
            }

            var node = new GameObject(name);
            node.transform.SetParent(parent, false);
            node.transform.localRotation = rotation;
            node.transform.localScale = scale;
            node.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            return node.AddComponent<MeshRenderer>();
        }
    }
}
