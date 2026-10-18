using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StaticInstances.Editor.Tests
{
    // The ladder's boundary array is grown by a user edit, never by a
    // repaint.
    //
    // With the ladder on and the array shorter than the window, a repaint that
    // grew the array would write serialized data with no user input, and
    // `Undo.PerformUndo()` would not take it back: every Ctrl+Z landing on a short
    // array would be re-written by the next repaint, so undo could never make progress.
    //
    // The state itself is legal and reachable (a hand-edited scene, a Preset, a
    // script raising the window, an undo), and the runtime handles it
    // deliberately (it skips the ladder and warns naming both counts), so
    // the Inspector reports it and offers the grow as an action rather than
    // performing it behind the reader's back.
    public sealed class StaticInstanceLadderGrowTests
    {
        private Scene testScene;
        private bool testSceneOpen;

        [SetUp]
        public void SetUp()
        {
            testScene = EditorSceneManager.NewPreviewScene();
            testSceneOpen = true;
        }

        [TearDown]
        public void TearDown()
        {
            if (testSceneOpen)
            {
                EditorSceneManager.ClosePreviewScene(testScene);
                testSceneOpen = false;
            }
        }

        // The everyday path: raise Max, get the boundaries. (That a REPAINT
        // does not write needs a live Inspector and cannot be run from a test.)
        [Test]
        public void TheEditPath_FillsTheWindow_FromEmpty()
        {
            StaticInstanceScatter scatter = NewScatter(min: 0, max: 2, distances: 0);
            var so = new SerializedObject(scatter);

            // What a repaint does: read the window, read the array, and
            // decide what the bar may draw. Nothing writes.
            SerializedProperty distances = so.FindProperty("lodDistances");
            int before = distances.arraySize;
            StaticInstanceEditorCommon.GrowDistancesForWindow(
                so.FindProperty("minMeshLodLevel"),
                so.FindProperty("maxMeshLodLevel"),
                distances);
            so.ApplyModifiedProperties();

            // GrowDistancesForWindow is the EDIT path, so this one does write —
            // and that is the point of the pairing: growing happens where a
            // user action is, and the repaint path below has no such call.
            so.Update();
            Assert.AreEqual(
                2, so.FindProperty("lodDistances").arraySize,
                "The edit path must fill the window: raising Max supplies the missing boundaries.");
            Assert.AreEqual(0, before, "fixture: the array should have started empty");
        }

        [Test]
        public void TheEditPath_GrowsButNeverShrinks()
        {
            StaticInstanceScatter scatter = NewScatter(min: 0, max: 3, distances: 5);
            var so = new SerializedObject(scatter);

            StaticInstanceEditorCommon.GrowDistancesForWindow(
                so.FindProperty("minMeshLodLevel"),
                so.FindProperty("maxMeshLodLevel"),
                so.FindProperty("lodDistances"));
            so.ApplyModifiedProperties();
            so.Update();

            Assert.AreEqual(
                5, so.FindProperty("lodDistances").arraySize,
                "Narrowing the window is often a step in the middle of an edit; " +
                "truncating would throw the authored value away for good, and the " +
                "runtime ignores extras.");
        }

        // A mixed selection has no single window, so there is no single
        // boundary count to grow to — writing one component's answer onto
        // another's array is the multi-edit failure this guards.
        [Test]
        public void AMixedWindow_GrowsNothing()
        {
            StaticInstanceScatter a = NewScatter(min: 0, max: 1, distances: 0);
            StaticInstanceScatter b = NewScatter(min: 0, max: 3, distances: 0);
            var so = new SerializedObject(new Object[] { a, b });

            Assert.IsTrue(
                so.FindProperty("maxMeshLodLevel").hasMultipleDifferentValues,
                "fixture: the two windows should disagree");

            StaticInstanceEditorCommon.GrowDistancesForWindow(
                so.FindProperty("minMeshLodLevel"),
                so.FindProperty("maxMeshLodLevel"),
                so.FindProperty("lodDistances"));
            so.ApplyModifiedProperties();

            Assert.AreEqual(0, new SerializedObject(a).FindProperty("lodDistances").arraySize);
            Assert.AreEqual(0, new SerializedObject(b).FindProperty("lodDistances").arraySize);
        }

        // The other half of that rule: OnValidate must not grow the array
        // either, or the component itself becomes a silent writer (on undo, on
        // deserialization, or on any script edit).
        [Test]
        public void OnValidate_DoesNotGrowTheLadder()
        {
            StaticInstanceScatter scatter = NewScatter(min: 0, max: 3, distances: 1);

            // What OnValidate reacts to, without going through the Inspector:
            // a serialized change applied to the component.
            var so = new SerializedObject(scatter);
            so.FindProperty("castShadows").boolValue = false;
            so.ApplyModifiedProperties();

            Assert.AreEqual(
                1, new SerializedObject(scatter).FindProperty("lodDistances").arraySize,
                "OnValidate grew the boundary array. That write lands outside any " +
                "user action — including during an undo — which is the defect " +
                "this test guards against.");
        }

        // -- SIS does not fall back silently.

        [Test]
        public void AShortLadder_WarnsNamingBothCounts()
        {
            string message = StaticInstanceScatter.LadderShortfallWarning(
                "Foliage", lodEnabled: true, minLevel: 1, maxLevel: 3, distances: new[] { 50f });

            Assert.IsNotNull(
                message,
                "A Scatter with 1 of 2 boundaries authored registers PerOwner and " +
                "renders one level, so a warning is required. The Inspector's " +
                "shortfall box only covers edit time; a Preset or a script can supply the short ladder.");
            Assert.That(message, Does.Contain("1 of 2"), "both counts must be named");
            Assert.That(message, Does.Contain("Foliage"), "the owner must be named");
            Assert.That(
                message, Does.Contain("mesh LOD 1"),
                "the message must say which level it is pinned to — the window " +
                "bottom, not 0, which is what ApplyLodParams actually registers.");
        }

        [Test]
        public void AComplete_ADisabled_AndASingleLevelLadder_AreAllSilent()
        {
            Assert.IsNull(
                StaticInstanceScatter.LadderShortfallWarning(
                    "Complete", true, 0, 2, new[] { 50f, 100f }),
                "a fully authored ladder must say nothing");
            Assert.IsNull(
                StaticInstanceScatter.LadderShortfallWarning(
                    "Extras", true, 0, 1, new[] { 50f, 100f, 200f }),
                "extras are ignored by the runtime and are not a shortfall");
            Assert.IsNull(
                StaticInstanceScatter.LadderShortfallWarning("Off", false, 0, 3, null),
                "LOD off is not a shortfall — nothing was asked for");
            Assert.IsNull(
                StaticInstanceScatter.LadderShortfallWarning("Single", true, 2, 2, null),
                "a single-level window has nothing to switch between");
        }

        private StaticInstanceScatter NewScatter(int min, int max, int distances)
        {
            var go = new GameObject($"Ladder_{min}_{max}_{distances}");
            SceneManager.MoveGameObjectToScene(go, testScene);
            var scatter = go.AddComponent<StaticInstanceScatter>();
            var so = new SerializedObject(scatter);
            so.FindProperty("lodEnabled").boolValue = true;
            so.FindProperty("minMeshLodLevel").intValue = min;
            so.FindProperty("maxMeshLodLevel").intValue = max;
            SerializedProperty array = so.FindProperty("lodDistances");
            array.arraySize = distances;
            for (int i = 0; i < distances; i++)
                array.GetArrayElementAtIndex(i).floatValue = 50f * (i + 1);
            so.ApplyModifiedPropertiesWithoutUndo();
            return scatter;
        }
    }
}
