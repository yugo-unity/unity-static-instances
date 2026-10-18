using NUnit.Framework;

namespace StaticInstances.Editor.Tests
{
    // The ratio ladder's authoring guards.
    //
    // A ratio ladder is DESCENDING where the distance-ladder guards assume
    // ASCENDING, so each comparison is the inverse of its `lodDistances`
    // counterpart. A single flipped operator lets a boundary pass its neighbour,
    // giving a level zero extent that the state machine can never settle in;
    // that failure raises no exception and logs nothing.
    public sealed class StaticInstanceRatioLadderGuardTests
    {
        private static float Clamp(float[] ratios, int index, float value, float cullRatio = 0f) =>
            StaticInstanceEditorCommon.ClampRatioBoundary(
                ratios, index, ratios.Length, value, cullRatio);

        [Test]
        public void ADescendingLadder_IsValid_AndAnAscendingOneIsNot()
        {
            Assert.IsTrue(
                StaticInstanceEditorCommon.IsRatioLadderDescending(
                    new[] { 0.5f, 0.25f, 0.1f }, 3),
                "1.0 is near and small ratios are far, so a valid ladder descends");
            Assert.IsFalse(
                StaticInstanceEditorCommon.IsRatioLadderDescending(
                    new[] { 0.1f, 0.25f, 0.5f }, 3),
                "an ascending ratio ladder is the inverted-guard bug, not a ladder");
        }

        [Test]
        public void EqualNeighbours_AreRefused_BecauseThatLevelHasZeroExtent()
        {
            // The same state the ascending guard refuses, stated on this axis:
            // two boundaries at one ratio means a level the state machine can
            // never settle in.
            Assert.IsFalse(
                StaticInstanceEditorCommon.IsRatioLadderDescending(
                    new[] { 0.5f, 0.5f }, 2),
                "strictly descending — equal neighbours are not allowed");
        }

        [Test]
        public void OnlyTheLIVEBoundaries_AreJudged()
        {
            // The array grows and is never shrunk, so entries past the live
            // boundary count are stale authored values, not ladder members.
            // Judging them would report a valid ladder as broken.
            Assert.IsTrue(
                StaticInstanceEditorCommon.IsRatioLadderDescending(
                    new[] { 0.5f, 0.25f, 9f, 9f }, 2),
                "entries past boundaryCount must be ignored");
        }

        [Test]
        public void ABoundary_CannotPassItsNeighbours_InEitherDirection()
        {
            var ratios = new[] { 0.8f, 0.5f, 0.2f };

            // Middle boundary pushed up past its NEARER neighbour (0.8).
            float up = Clamp(ratios, 1, 0.95f);
            Assert.Less(up, 0.8f, "a boundary must stay below its nearer neighbour");

            // ...and down past its FARTHER neighbour (0.2).
            float down = Clamp(ratios, 1, 0.05f);
            Assert.Greater(down, 0.2f, "a boundary must stay above its farther neighbour");
        }

        [Test]
        public void TheFirstBoundary_IsCappedAtOne_AndTheLastAtTheFloor()
        {
            var ratios = new[] { 0.8f, 0.5f };

            Assert.LessOrEqual(
                Clamp(ratios, 0, 5f), 1f,
                "a ratio is a fraction of screen height — 1.0 is the maximum");
            Assert.Greater(
                Clamp(ratios, 1, -3f), 0f,
                "a ratio at or below 0 would be the no-limit sentinel, not a boundary");
        }

        [Test]
        public void TheCulledBoundary_PushesLodBoundariesAwayFromIt()
        {
            // The Culled ratio is the FAR end of this axis, so it is a LOWER
            // bound here where the distance axis has an upper one — the exact
            // inversion at issue here.
            var ratios = new[] { 0.8f, 0.5f };
            float clamped = Clamp(ratios, 1, 0.3f, 0.35f);

            Assert.Greater(
                clamped, 0.35f,
                "no LOD boundary may sit at or beyond the Culled ratio, or the " +
                "level it opens can never be seen");
        }

        [Test]
        public void ADegenerateWindow_HoldsTheValue_RatherThanSnappingIt()
        {
            // Neighbours already adjacent: there is nowhere valid to put the
            // boundary, and snapping it somewhere the author did not ask for
            // is worse than leaving it.
            var ratios = new[] { 0.5001f, 0.5f, 0.4999f };
            float clamped = Clamp(ratios, 1, 0.5f);

            Assert.Greater(clamped, 0f);
            Assert.LessOrEqual(clamped, 1f);
        }

        [Test]
        public void TheCullRatio_StaysStrictlyBelowTheCoarsestBoundary()
        {
            var ratios = new[] { 0.8f, 0.25f };

            float clamped = StaticInstanceEditorCommon.ClampCullRatio(ratios, 2, 0.5f);
            Assert.Less(
                clamped, 0.25f,
                "culling before the coarsest level is reachable would make that " +
                "level unreachable");

            Assert.AreEqual(
                0f, StaticInstanceEditorCommon.ClampCullRatio(ratios, 2, 0f),
                "0 stays 0 — it is the expressible 'unauthored', not a cutoff");
        }

        [Test]
        public void GrowingTheLadder_HalvesEachNewBoundary()
        {
            // The descending twin of GrowDistances' doubling: half the screen
            // coverage is roughly twice the distance, so the spacing reads the
            // same on either axis.
            var so = new UnityEditor.SerializedObject(
                UnityEngine.ScriptableObject.CreateInstance<RatioHolder>());
            try
            {
                UnityEditor.SerializedProperty ratios = so.FindProperty("ratios");
                StaticInstanceEditorCommon.GrowRatios(ratios, 3);

                Assert.AreEqual(3, ratios.arraySize);
                Assert.AreEqual(0.5f, ratios.GetArrayElementAtIndex(0).floatValue, 1e-5f,
                    "the first boundary starts at 0.5, not 1.0 — a boundary AT 1.0 " +
                    "would leave level 0 the moment the instance stops filling the screen");
                Assert.AreEqual(0.25f, ratios.GetArrayElementAtIndex(1).floatValue, 1e-5f);
                Assert.AreEqual(0.125f, ratios.GetArrayElementAtIndex(2).floatValue, 1e-5f);
                Assert.IsTrue(
                    StaticInstanceEditorCommon.IsRatioLadderDescending(
                        new[]
                        {
                            ratios.GetArrayElementAtIndex(0).floatValue,
                            ratios.GetArrayElementAtIndex(1).floatValue,
                            ratios.GetArrayElementAtIndex(2).floatValue,
                        },
                        3),
                    "a grown ladder must be valid without the author touching it");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(so.targetObject);
            }
        }

        [Test]
        public void GrowingNeverShrinks_SoAnEditInProgressKeepsItsValues()
        {
            var so = new UnityEditor.SerializedObject(
                UnityEngine.ScriptableObject.CreateInstance<RatioHolder>());
            try
            {
                UnityEditor.SerializedProperty ratios = so.FindProperty("ratios");
                StaticInstanceEditorCommon.GrowRatios(ratios, 3);
                float third = ratios.GetArrayElementAtIndex(2).floatValue;

                // Growing to a SMALLER count must not truncate: lowering the
                // level count is often a step in the middle of an edit.
                StaticInstanceEditorCommon.GrowRatios(ratios, 2);
                Assert.GreaterOrEqual(
                    ratios.arraySize, 2,
                    "the live boundaries must still be there");
                if (ratios.arraySize > 2)
                {
                    Assert.AreEqual(
                        third, ratios.GetArrayElementAtIndex(2).floatValue, 1e-5f,
                        "an authored value beyond the live count must survive");
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(so.targetObject);
            }
        }

        private sealed class RatioHolder : UnityEngine.ScriptableObject
        {
            [UnityEngine.SerializeField] private float[] ratios = new float[0];
        }
    }
}
