using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StaticInstances.Editor.Tests
{
    // The Culled bands the Distances bar draws at either end.
    //
    // The bands themselves are pixels, but the two numbers behind them are not,
    // and both have a silent failure mode. The axis has to reach PAST the far
    // limit or the band is a zero-width sliver at the right edge — and the
    // feature's whole claim is that "the segment's presence reads as culling is
    // authored". The limit reader has to answer 0 for a mixed selection, or the
    // bar draws one component's limit over another's ladder.
    public sealed class StaticInstanceLodBarGeometryTests
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

        [Test]
        public void WithNoFarLimit_TheAxisIsOneAndAHalfTimesTheLastBoundary()
        {
            SerializedProperty distances = Ladder(50f, 100f);

            Assert.AreEqual(
                150f,
                StaticInstanceEditorCommon.LodBarAxisMax(distances, 2, 0f),
                0.001f,
                "Without a Draw Range Max the axis is 1.5x the last boundary.");
        }

        [Test]
        public void AFarLimitBeyondTheLadder_ExtendsTheAxisPastIt()
        {
            SerializedProperty distances = Ladder(50f, 100f);

            float axisMax = StaticInstanceEditorCommon.LodBarAxisMax(distances, 2, 400f);

            Assert.Greater(
                axisMax, 400f,
                "The axis stops at the far limit, so the Culled band would be a " +
                "zero-width sliver at the right edge and the reader could not see " +
                "that culling is authored at all.");
        }

        // The case the feature exists to make visible: a boundary sitting
        // beyond the cull distance is a level that can never draw. The axis
        // must NOT shrink to the limit — the boundary has to stay on screen,
        // inside the band, which is what shows the mistake.
        [Test]
        public void AFarLimitInsideTheLadder_KeepsTheLadderVisible()
        {
            SerializedProperty distances = Ladder(50f, 100f);

            float axisMax = StaticInstanceEditorCommon.LodBarAxisMax(distances, 2, 60f);

            Assert.AreEqual(
                150f, axisMax, 0.001f,
                "A near cull distance must not pull the axis in over the ladder: " +
                "the boundary at 100 has to remain visible inside the Culled band, " +
                "because that is the authoring mistake the band is there to show.");
        }

        [Test]
        public void AMixedSelection_DrawsNoBand_RatherThanOneComponentsLimit()
        {
            var a = NewScatter("A");
            var b = NewScatter("B");
            SetCull(a, 250f);
            SetCull(b, 700f);

            var so = new SerializedObject(new Object[] { a, b });
            Assert.IsTrue(
                so.FindProperty("cullDistance").hasMultipleDifferentValues,
                "fixture: the two components should disagree by now");
            Assert.AreEqual(
                0f,
                StaticInstanceEditorCommon.BarCullDistance(so.FindProperty("cullDistance")),
                "With no single limit there is no band to draw — 0 is the value " +
                "that means 'no band', which is also what unlimited means.");
        }

        [Test]
        public void ASingleSelectionsLimit_IsReadStraightThrough()
        {
            var group = NewScatter("Single");
            var so = new SerializedObject(group);
            so.FindProperty("cullDistance").floatValue = 320f;
            so.ApplyModifiedProperties();
            so.Update();

            Assert.AreEqual(
                320f,
                StaticInstanceEditorCommon.BarCullDistance(so.FindProperty("cullDistance")),
                0.001f);
        }

        private static void SetCull(StaticInstanceScatter group, float value)
        {
            var so = new SerializedObject(group);
            so.FindProperty("cullDistance").floatValue = value;
            so.ApplyModifiedProperties();
        }

        private SerializedProperty Ladder(params float[] boundaries)
        {
            var group = NewScatter("Ladder");
            var so = new SerializedObject(group);
            SerializedProperty distances = so.FindProperty("lodDistances");
            distances.arraySize = boundaries.Length;
            for (int i = 0; i < boundaries.Length; i++)
                distances.GetArrayElementAtIndex(i).floatValue = boundaries[i];
            so.ApplyModifiedProperties();
            so.Update();
            return so.FindProperty("lodDistances");
        }

        private StaticInstanceScatter NewScatter(string name)
        {
            var go = new GameObject(name);
            SceneManager.MoveGameObjectToScene(go, testScene);
            return go.AddComponent<StaticInstanceScatter>();
        }
    }
}
