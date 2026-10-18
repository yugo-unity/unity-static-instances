using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace StaticInstances.Editor.Tests
{
    public sealed class StaticInstanceInspectorCacheInvalidationTests
    {
        GameObject ownerObject;
        readonly List<Object> assets = new List<Object>();
        StaticInstanceScatter Owner()
        {
            ownerObject = new GameObject("cache-owner");
            return ownerObject.AddComponent<StaticInstanceScatter>();
        }
        Mesh Mesh(string name)
        {
            var mesh = new Mesh { name = name }; assets.Add(mesh); return mesh;
        }
        [TearDown] public void TearDown()
        {
            if (ownerObject != null) Object.DestroyImmediate(ownerObject);
            foreach (Object asset in assets) if (asset != null) Object.DestroyImmediate(asset);
            assets.Clear();
        }

        [Test]
        public void MissingLevelCacheHitsUntilExplicitlyInvalidatedAtSameCount()
        {
            var owner = Owner(); var cache = new StaticInstanceEditorCommon.MissingLevelCache();
            Mesh first = Mesh("first"), second = Mesh("second"); int calls = 0; Mesh current = first;
            void Collect(Dictionary<Mesh, int> result) { calls++; result.Add(current, 4); }
            cache.Refresh(owner, 2, 4, Collect);
            Assert.That(cache.List, Does.Contain("first"));
            current = second; cache.Refresh(owner, 2, 4, Collect);
            Assert.That(calls, Is.EqualTo(1)); Assert.That(cache.List, Does.Contain("first"));
            cache.Invalidate(); cache.Refresh(owner, 2, 4, Collect);
            Assert.That(calls, Is.EqualTo(2)); Assert.That(cache.List, Does.Contain("second"));
        }

        [Test]
        public void SpreadRatioCacheRefreshesSameCountBoundsOnlyAfterInvalidation()
        {
            var owner = Owner(); var cache = new StaticInstanceEditorCommon.SpreadRatioCache();
            Apply(owner, new Bounds(Vector3.zero, Vector3.one), new Bounds(Vector3.right, Vector3.one));
            cache.Refresh(owner, 2); float first = cache.Diagonal;
            Apply(owner, new Bounds(Vector3.zero, Vector3.one), new Bounds(Vector3.right * 20f, Vector3.one));
            cache.Refresh(owner, 2); Assert.That(cache.Diagonal, Is.EqualTo(first));
            cache.Invalidate(); cache.Refresh(owner, 2);
            Assert.That(cache.Diagonal, Is.GreaterThan(first));
        }

        [TestCase("OnHierarchyChanged")]
        [TestCase("OnUndoRedoPerformed")]
        public void ScatterEditorInvalidationCallbacksClearBothDerivedCaches(string callback)
        {
            var owner = Owner();
            var editor = UnityEditor.Editor.CreateEditor(owner, typeof(StaticInstanceScatterEditor));
            try
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var missing = (StaticInstanceEditorCommon.MissingLevelCache)typeof(StaticInstanceScatterEditor)
                    .GetField("missingLevelCache", flags).GetValue(editor);
                var spread = (StaticInstanceEditorCommon.SpreadRatioCache)typeof(StaticInstanceScatterEditor)
                    .GetField("spreadRatioCache", flags).GetValue(editor);
                Mesh first = Mesh("first"), second = Mesh("second"); int calls = 0; Mesh current = first;
                missing.Refresh(owner, 2, 4, result => { calls++; result.Add(current, 4); });
                Apply(owner, new Bounds(Vector3.zero, Vector3.one), new Bounds(Vector3.right, Vector3.one));
                spread.Refresh(owner, 2); float firstDiagonal = spread.Diagonal;
                current = second;
                Apply(owner, new Bounds(Vector3.zero, Vector3.one), new Bounds(Vector3.right * 20f, Vector3.one));
                typeof(StaticInstanceScatterEditor).GetMethod(callback, flags).Invoke(editor, null);
                missing.Refresh(owner, 2, 4, result => { calls++; result.Add(current, 4); });
                spread.Refresh(owner, 2);
                Assert.That(calls, Is.EqualTo(2)); Assert.That(missing.List, Does.Contain("second"));
                Assert.That(spread.Diagonal, Is.GreaterThan(firstDiagonal));
            }
            finally { Object.DestroyImmediate(editor); }
        }

        static void Apply(StaticInstanceScatter owner, params Bounds[] bounds)
        {
            owner.ApplyBakedData(new Matrix4x4[bounds.Length], bounds,
                new BakedDrawGroup[1], new int[bounds.Length]);
        }
    }
}
