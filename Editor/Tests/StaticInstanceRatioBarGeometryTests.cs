using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace StaticInstances.Editor.Tests
{
    // The ratio bar's geometry, and the two array rules behind
    // it. The bar is pixels; these three things are not, and each fails
    // silently:
    //
    //   (1) THE AXIS ROUND TRIP. The bar DRAWS a boundary with one mapping and
    //       DRAGS it with the inverse. If those stop being inverses the handle
    //       slides out from under the cursor — no exception, no log, no failing
    //       frame. ⚠ The mapping is also the one place the DESCENDING axis is
    //       expressed, and every distance-ladder guard assumes
    //       ASCENDING.
    //
    //   (2) THE ARRAY GROWS AND IS NEVER SHRUNK, and it grows only on a user
    //       edit. An inspector repaint does not write serialized data.
    //       ⚠ Drawing the ladder must not call GrowRatios unguarded: a
    //       repaint would write, and narrowing the LOD Level Range would
    //       TRUNCATE the ladder.
    //
    //   (3) A MIXED SELECTION HAS NO BAR TO DRAW. The agreement test decides
    //       whether the real bar or the mixed placeholder is drawn, and a false
    //       "agree" paints one component's ladder over another's.
    //
    // ⚠ What is deliberately NOT called here: `DrawRatioLadderShortfall` and
    // `DrawScreenSizeLadder` themselves. Both go through EditorGUILayout, which
    // only runs inside OnGUI — the same reason
    // StaticInstanceLodBarGeometryTests tests the distance bar's numbers and
    // not its drawing. The rules those two enforce are the seams below.
    public sealed class StaticInstanceRatioBarGeometryTests
    {
        private RatioHolder holder;

        [TearDown]
        public void TearDown()
        {
            if (holder != null)
                Object.DestroyImmediate(holder);
        }

        private SerializedObject NewHolder(params float[] values)
        {
            holder = ScriptableObject.CreateInstance<RatioHolder>();
            var so = new SerializedObject(holder);
            SerializedProperty ratios = so.FindProperty("ratios");
            ratios.arraySize = values.Length;
            for (int i = 0; i < values.Length; i++)
                ratios.GetArrayElementAtIndex(i).floatValue = values[i];
            so.ApplyModifiedPropertiesWithoutUndo();
            return so;
        }

        // -- (1) the axis ------------------------------------------------------

        [Test]
        public void TheAxisRunsFromOneAtTheLeftEdgeToZeroAtTheRight()
        {
            // The bar's layout, read left to right: |LOD 0|LOD 1|Culled|
            // over 1.0 / 0.5 / 0.25 / 0.0.
            Assert.AreEqual(
                0f, StaticInstanceEditorCommon.RatioBarFraction(1f), 1e-6f,
                "ratio 1.0 is the near end and sits at the left edge");
            Assert.AreEqual(
                1f, StaticInstanceEditorCommon.RatioBarFraction(0f), 1e-6f,
                "ratio 0.0 is the far end and sits at the right edge");
            Assert.AreEqual(
                0.5f, StaticInstanceEditorCommon.RatioBarFraction(0.5f), 1e-6f,
                "the axis is LINEAR in ratio — required, not a free choice");
        }

        [Test]
        public void TheAxisDescends_SoALargerRatioDrawsLeftOfASmallerOne()
        {
            // The inversion expressed as geometry. On the distance bar the relation
            // is the other way round, and the two bars sit in the same band of
            // the same inspector.
            Assert.Less(
                StaticInstanceEditorCommon.RatioBarFraction(0.8f),
                StaticInstanceEditorCommon.RatioBarFraction(0.2f),
                "near (large ratio) is left, far (small ratio) is right");
        }

        [Test]
        public void DrawingAndDraggingAreExactInverses()
        {
            // The defect this pins has no symptom other than a handle that will
            // not follow the mouse.
            foreach (float ratio in new[] { 0f, 0.001f, 0.25f, 0.5f, 0.75f, 1f })
            {
                float fraction = StaticInstanceEditorCommon.RatioBarFraction(ratio);
                Assert.AreEqual(
                    ratio,
                    StaticInstanceEditorCommon.RatioAtBarFraction(fraction),
                    1e-6f,
                    $"ratio {ratio} must survive the round trip");
            }
        }

        [Test]
        public void TheAxisIsBounded_SoAStrayValueCannotLeaveTheBar()
        {
            // Unlike the distance bar, the axis does NOT rescale to its
            // content, so an out-of-range value must clamp rather than push the
            // axis out from under everything else.
            Assert.AreEqual(0f, StaticInstanceEditorCommon.RatioBarFraction(9f), 1e-6f);
            Assert.AreEqual(1f, StaticInstanceEditorCommon.RatioBarFraction(-3f), 1e-6f);
            Assert.AreEqual(1f, StaticInstanceEditorCommon.RatioAtBarFraction(-0.2f), 1e-6f);
            Assert.AreEqual(0f, StaticInstanceEditorCommon.RatioAtBarFraction(4f), 1e-6f);
        }

        // -- (2) the array's two rules ----------------------------------------

        [Test]
        public void GrowingNeverShrinks_EvenWhenAskedTo()
        {
            // Pinned at the seam: `arraySize = count`
            // truncates as readily as it grows, so calling it unguarded on every
            // repaint from the ladder's draw path would let narrowing the LOD
            // Level Range destroy authored boundaries with no way back.
            SerializedObject so = NewHolder(0.5f, 0.25f, 0.125f);
            SerializedProperty ratios = so.FindProperty("ratios");

            StaticInstanceEditorCommon.GrowRatios(ratios, 1);

            Assert.AreEqual(
                3, ratios.arraySize,
                "lowering the level count is a step mid-edit, not a delete");
            Assert.AreEqual(
                0.125f, ratios.GetArrayElementAtIndex(2).floatValue, 1e-6f,
                "the authored value beyond the live count must survive");
        }

        [Test]
        public void TheWindowEditGrowsTheLadder_ToTheWindowsBoundaryCount()
        {
            SerializedObject so = NewHolder(0.5f);
            SerializedProperty ratios = so.FindProperty("ratios");
            SerializedProperty min = so.FindProperty("minLevel");
            SerializedProperty max = so.FindProperty("maxLevel");
            min.intValue = 0;
            max.intValue = 3;

            StaticInstanceEditorCommon.GrowRatiosForWindow(min, max, ratios);

            Assert.AreEqual(
                3, ratios.arraySize,
                "a window spanning three boundaries needs three of them");
            Assert.AreEqual(
                0.5f, ratios.GetArrayElementAtIndex(0).floatValue, 1e-6f,
                "the authored boundary must not be rewritten");
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

        [Test]
        public void TheWindowEditNeverShrinksTheLadder()
        {
            SerializedObject so = NewHolder(0.5f, 0.25f, 0.125f);
            SerializedProperty ratios = so.FindProperty("ratios");
            SerializedProperty min = so.FindProperty("minLevel");
            SerializedProperty max = so.FindProperty("maxLevel");
            min.intValue = 0;
            max.intValue = 1;

            StaticInstanceEditorCommon.GrowRatiosForWindow(min, max, ratios);

            Assert.AreEqual(3, ratios.arraySize);
        }

        [Test]
        public void TheWindowEditIsInertOnAMixedSelection_AndOnANullLadder()
        {
            // A mixed window has no single boundary count, so writing one would
            // impose one selection member's window on the rest.
            SerializedObject so = NewHolder(0.5f);
            SerializedProperty ratios = so.FindProperty("ratios");
            SerializedProperty min = so.FindProperty("minLevel");
            SerializedProperty max = so.FindProperty("maxLevel");

            Assert.DoesNotThrow(
                () => StaticInstanceEditorCommon.GrowRatiosForWindow(min, max, null),
                "a component with no ratio ladder must not throw on a window edit");
            Assert.AreEqual(1, ratios.arraySize);
        }

        // -- (3) agreement -----------------------------------------------------

        [Test]
        public void ASingleSelection_AlwaysAgrees()
        {
            SerializedObject so = NewHolder(0.5f, 0.25f);

            Assert.IsTrue(
                StaticInstanceEditorCommon.LodRatiosAgree(
                    so.FindProperty("ratios"), so.FindProperty("cullRatio"), 2));
        }

        [Test]
        public void AgreementToleratesANullCullRatio()
        {
            // The Culled boundary is optional on the caller's side, and a null
            // property must read as "nothing to disagree about" rather than
            // throwing inside a repaint.
            SerializedObject so = NewHolder(0.5f, 0.25f);

            Assert.IsTrue(
                StaticInstanceEditorCommon.LodRatiosAgree(
                    so.FindProperty("ratios"), null, 2));
        }

        [Test]
        public void AgreementOnlyJudgesTheBoundariesTheBarWouldDraw()
        {
            // The array grows and is never shrunk, so entries past the live
            // count are stale authored values, not ladder members — judging
            // them would send an agreeing selection to the mixed placeholder.
            SerializedObject so = NewHolder(0.5f, 0.25f, 0.125f);

            Assert.IsTrue(
                StaticInstanceEditorCommon.LodRatiosAgree(
                    so.FindProperty("ratios"), null, 2));
        }

        // -- (4) the percent display ---

        [Test]
        public void ThePercentDisplayIsAPureDisplayLayer_AndRoundTrips()
        {
            // ⚠ Storage stays a ratio float; only the rows and readouts speak
            // percent. The failure mode these pin has no clamp to catch it:
            // a value written back 100 times too large or too small is still a legal ratio at
            // one end and a legal percent at the other, so nothing reports it.
            Assert.AreEqual(100f, StaticInstanceEditorCommon.RatioToPercent(1f), 1e-4f);
            Assert.AreEqual(50f, StaticInstanceEditorCommon.RatioToPercent(0.5f), 1e-4f);
            Assert.AreEqual(25f, StaticInstanceEditorCommon.RatioToPercent(0.25f), 1e-4f);
            Assert.AreEqual(0f, StaticInstanceEditorCommon.RatioToPercent(0f), 1e-4f);

            foreach (float ratio in new[] { 0f, 0.0001f, 0.125f, 0.5f, 0.99f, 1f })
            {
                Assert.AreEqual(
                    ratio,
                    StaticInstanceEditorCommon.PercentToRatio(
                        StaticInstanceEditorCommon.RatioToPercent(ratio)),
                    1e-6f,
                    $"ratio {ratio} must survive the display round trip");
            }
        }

        [Test]
        public void TheAuthorableFloor_IsExpressibleInPercent()
        {
            // The rows' slider minimum is the floor converted, so a floor that
            // rounded to 0 in percent would let the ladder be authored at a
            // ratio the clamps refuse.
            Assert.Greater(
                StaticInstanceEditorCommon.RatioToPercent(
                    StaticInstanceEditorCommon.MinAuthorableRatio),
                0f,
                "the authorable floor must not display as zero percent");
        }

        // -- (5) Culled must not displace a boundary -

        [Test]
        public void TheCullClampStopsAtTheLastBoundary_AndNeverPushesIt()
        {
            // The clamp stops Culled below the last boundary without pushing it.
            var ratios = new[] { 0.5f, 0.25f };

            float clamped = StaticInstanceEditorCommon.ClampCullRatio(ratios, 2, 0.4f);

            Assert.Less(
                clamped, 0.25f,
                "the Culled ratio must stop BELOW the coarsest boundary");
            Assert.AreEqual(
                0.5f, ratios[0], 1e-6f, "no LOD boundary may be displaced");
            Assert.AreEqual(
                0.25f, ratios[1], 1e-6f, "no LOD boundary may be displaced");
        }

        [Test]
        public void DraggingTheCulledHandleToTheEdge_UnauthorsIt()
        {
            // The unconditional Culled handle's symmetry: dragging OUT of the right edge authors
            // the cull, dragging back to it removes it. `ClampCullRatio`'s 0 is
            // the expressible "unauthored", not a cutoff at zero coverage.
            var ratios = new[] { 0.5f, 0.25f };
            float atEdge = StaticInstanceEditorCommon.RatioAtBarFraction(1f);

            Assert.AreEqual(
                0f, StaticInstanceEditorCommon.ClampCullRatio(ratios, 2, atEdge),
                "the parked position must read as unauthored");
        }

        // -- (6) GrowDistances ----------

        [Test]
        public void GrowDistancesRefusesToShrink_LikeItsRatioTwin()
        {
            // GrowDistances never truncates, matching GrowRatios.
            SerializedObject so = NewHolder(50f, 100f, 200f);
            SerializedProperty distances = so.FindProperty("ratios");

            StaticInstanceEditorCommon.GrowDistances(distances, 1);

            Assert.AreEqual(
                3, distances.arraySize,
                "lowering the level count is a step mid-edit, not a delete");
            Assert.AreEqual(
                200f, distances.GetArrayElementAtIndex(2).floatValue, 1e-4f,
                "the authored value beyond the live count must survive");
        }

        // Boundary readouts and segment labels share one fit predicate
        // (`BarLabelFits`), so they appear and disappear at the same widths.
        // This test pins that predicate and its threshold.
        [Test]
        public void BarLabelFits_IsOneSharedRuleWithAStrictThreshold()
        {
            Assert.IsFalse(
                StaticInstanceEditorCommon.BarLabelFits(
                    StaticInstanceEditorCommon.BarLabelMinWidth),
                "The threshold is exclusive: a segment exactly at the minimum gets " +
                "no label. Segment labels and boundary readouts use the same " +
                "predicate so they never disagree.");
            Assert.IsTrue(
                StaticInstanceEditorCommon.BarLabelFits(
                    StaticInstanceEditorCommon.BarLabelMinWidth + 0.01f),
                "Just past the minimum must fit.");
            Assert.IsFalse(
                StaticInstanceEditorCommon.BarLabelFits(0f),
                "A zero-width segment cannot carry a label.");
            Assert.IsFalse(
                StaticInstanceEditorCommon.BarLabelFits(-5f),
                "A negative width is a collapsed segment, not a wide one — the " +
                "readout's owning-segment width is computed as a difference and " +
                "can go negative if boundaries cross.");
        }

        private sealed class RatioHolder : ScriptableObject
        {
            [SerializeField] private float[] ratios = new float[0];
            [SerializeField] private float cullRatio;
            [SerializeField] private int minLevel;
            [SerializeField] private int maxLevel;
        }
    }
}
