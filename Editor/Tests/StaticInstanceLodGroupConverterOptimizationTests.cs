using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace StaticInstances.Editor.Tests
{
    public sealed class StaticInstanceLodGroupConverterOptimizationTests
    {
        readonly List<GameObject> objects = new List<GameObject>();
        GameObject Make(string name, Transform parent = null, bool active = true)
        {
            var value = new GameObject(name);
            objects.Add(value);
            if (parent != null) value.transform.SetParent(parent, false);
            value.SetActive(active);
            return value;
        }
        MeshRenderer Renderer(string name, Transform parent, bool active = true)
            => Make(name, parent, active).AddComponent<MeshRenderer>();
        [TearDown] public void TearDown()
        {
            Undo.ClearAll();
            for (int i = objects.Count - 1; i >= 0; i--)
                if (objects[i] != null) Object.DestroyImmediate(objects[i]);
            objects.Clear();
        }

        [Test]
        public void IndexMatchesReferenceForNestedInactiveAndSharedReferences()
        {
            var root = Make("root"); var coarse = Renderer("coarse", root.transform);
            var middle = Make("middle", coarse.transform); var kept = Renderer("kept", middle.transform, false);
            var keep = new HashSet<Renderer> { kept, kept };
            var index = StaticInstanceLodGroupConverter.BuildKeptAncestorIndex(keep);
            Assert.That(index.Contains(coarse.transform), Is.True);
            Assert.That(ReferenceContains(coarse.transform, keep), Is.True);
            AssertEquivalent(new[] { root.transform, coarse.transform, middle.transform, kept.transform }, keep);
        }

        [Test]
        public void IndexPreservesExternalLodRendererReferences()
        {
            var groupRoot = Make("group"); var externalRoot = Make("external");
            var coarseAncestor = Renderer("external-coarse", externalRoot.transform);
            var kept = Renderer("external-kept", coarseAncestor.transform);
            var index = StaticInstanceLodGroupConverter.BuildKeptAncestorIndex(new HashSet<Renderer> { kept });
            Assert.That(index.Contains(coarseAncestor.transform), Is.True);
            Assert.That(index.Contains(groupRoot.transform), Is.False);
        }

        [Test]
        public void ExecuteKeepsAncestorAndDestroysUnrelatedCoarseObject()
        {
            var root = Make("root"); var group = root.AddComponent<LODGroup>();
            var ancestor = Renderer("ancestor", root.transform); var kept = Renderer("kept", ancestor.transform, false);
            var unrelated = Renderer("unrelated", root.transform);
            group.SetLODs(new[] { new LOD(.5f, new Renderer[] { kept }), new LOD(.1f, new Renderer[] { ancestor, unrelated }) });
            var plan = StaticInstanceLodGroupConverter.Analyze(new[] { root }, 60f);
            Assert.That(plan.Groups, Is.EquivalentTo(new[] { group }));
            Assert.That(plan.Doomed.Exists(row => row.Object == unrelated.gameObject), Is.True);
            Assert.That(plan.Doomed.Exists(row => row.Object == ancestor.gameObject), Is.False);
            Assert.That(StaticInstanceLodGroupConverter.Execute(plan, false), Is.EqualTo(1));
            Assert.That(ancestor == null, Is.True); Assert.That(kept == null, Is.False);
            Assert.That(unrelated == null, Is.True);
        }

        [Test]
        public void ProtectedCoarseObjectAndUndoBehaviorRemainIntact()
        {
            var root = Make("root"); var group = root.AddComponent<LODGroup>();
            var kept = Renderer("kept", root.transform); var coarse = Renderer("coarse", root.transform);
            group.SetLODs(new[] { new LOD(.5f, new Renderer[] { kept }), new LOD(.1f, new Renderer[] { coarse }) });
            var plan = new StaticInstanceLodGroupConverter.Plan(); plan.Groups.Add(group);
            GameObject coarseObject = coarse.gameObject;
            StaticInstanceLodGroupConverter.Execute(plan, true, new HashSet<GameObject> { coarseObject });
            Assert.That(coarseObject == null, Is.False); Assert.That(coarse == null, Is.True);
            Undo.PerformUndo();
            Assert.That(root.GetComponent<LODGroup>() == null, Is.False);
            Assert.That(coarseObject.GetComponent<MeshRenderer>() == null, Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void IndexMatchesReferenceForDeepAndFlatHierarchies(bool deep)
        {
            var root = Make(deep ? "deep" : "flat");
            var queries = new List<Transform>();
            Transform parent = root.transform;
            for (int i = 0; i < 128; i++)
            {
                var renderer = Renderer("renderer-" + i, parent);
                queries.Add(renderer.transform);
                if (deep) parent = renderer.transform;
            }
            var keep = new HashSet<Renderer> { queries[queries.Count - 1].GetComponent<Renderer>() };
            AssertEquivalent(queries, keep);
        }

        static bool ReferenceContains(Transform root, HashSet<Renderer> keep)
        {
            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
                if (keep.Contains(renderer)) return true;
            return false;
        }

        static void AssertEquivalent(IEnumerable<Transform> queries, HashSet<Renderer> keep)
        {
            HashSet<Transform> index = StaticInstanceLodGroupConverter.BuildKeptAncestorIndex(keep);
            foreach (Transform query in queries)
                Assert.That(index.Contains(query), Is.EqualTo(ReferenceContains(query, keep)), query.name);
        }
    }
}
