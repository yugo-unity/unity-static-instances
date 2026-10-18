using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace StaticInstances.Editor.Tests
{
    // Draw Range Min at or past a non-zero Max is
    // unauthorable, and what the runtime does when it meets one anyway.
    //
    // The state these pin is the bad kind of wrong: because the thresholds
    // fold each instance's bounding radius in, an inverted pair does not blank
    // the owner — it erases the instances smaller than (Min − Max) / 2 and
    // leaves the rest, which reads in a scene as "some of my content is
    // missing" with nothing to point at. So both halves are driven here: the
    // Inspector's push (the edited field wins, its partner follows, per
    // target) and the runtime's belt (ignore Min, warn once, naming both).
    public sealed class StaticInstanceDrawRangeTests
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

        // -- The Inspector half: push, never clamp.

        [Test]
        public void RaisingMinPastMax_PushesMaxUp_AndKeepsTheTypedMin()
        {
            StaticInstanceScatter group = NewGroup("Pushed", min: 0f, max: 50f);
            var so = new SerializedObject(group);
            so.FindProperty("minDrawDistance").floatValue = 120f;
            so.ApplyModifiedProperties();

            StaticInstanceEditorCommon.PushDrawRangePartner(
                so.FindProperty("minDrawDistance"),
                so.FindProperty("cullDistance"),
                minEdited: true);

            so.Update();
            Assert.AreEqual(
                120f, so.FindProperty("minDrawDistance").floatValue, 0.001f,
                "The typed value was discarded; the Inspector must push the partner " +
                "field rather than clamp the edited one.");
            Assert.Greater(
                so.FindProperty("cullDistance").floatValue, 120f,
                "Max should have been pushed clear of the new Min, and clear " +
                "rather than equal: equal is the degenerate pair the runtime " +
                "belt warns about, so the Inspector must not author it.");
        }

        [Test]
        public void LoweringMaxBelowMin_PushesMinDown_AndKeepsTheTypedMax()
        {
            StaticInstanceScatter group = NewGroup("Pushed", min: 200f, max: 400f);
            var so = new SerializedObject(group);
            so.FindProperty("cullDistance").floatValue = 100f;
            so.ApplyModifiedProperties();

            StaticInstanceEditorCommon.PushDrawRangePartner(
                so.FindProperty("minDrawDistance"),
                so.FindProperty("cullDistance"),
                minEdited: false);

            so.Update();
            Assert.AreEqual(
                100f, so.FindProperty("cullDistance").floatValue, 0.001f,
                "the typed Max was not kept");
            Assert.Less(
                so.FindProperty("minDrawDistance").floatValue, 100f,
                "Min should have been pushed below the new Max.");
        }

        // 0 means unlimited on the far end, so nothing can be past it and
        // nothing may move. This is the case a naive "min >= max" test gets
        // wrong, and getting it wrong would push Max up the moment anyone
        // authors a near limit on ordinary content.
        [Test]
        public void AZeroMax_PushesNothing_BecauseUnlimitedConstrainsNothing()
        {
            StaticInstanceScatter group = NewGroup("Unlimited", min: 0f, max: 0f);
            var so = new SerializedObject(group);
            so.FindProperty("minDrawDistance").floatValue = 300f;
            so.ApplyModifiedProperties();

            StaticInstanceEditorCommon.PushDrawRangePartner(
                so.FindProperty("minDrawDistance"),
                so.FindProperty("cullDistance"),
                minEdited: true);

            so.Update();
            Assert.AreEqual(300f, so.FindProperty("minDrawDistance").floatValue, 0.001f);
            Assert.AreEqual(
                0f, so.FindProperty("cullDistance").floatValue, 0.001f,
                "Max = 0 is 'unlimited' and must stay it — pushing it to 300.1 " +
                "would silently impose a far limit the author never asked for.");
        }

        // The multi-edit clause: each component resolves against ITS OWN
        // partner. Pushing both to the first target's value is the failure
        // this exists to prevent, and it is invisible unless the selection
        // disagrees.
        [Test]
        public void AMultiSelection_PushesEachTargetAgainstItsOwnPartner()
        {
            StaticInstanceScatter near = NewGroup("Near", min: 0f, max: 50f);
            StaticInstanceScatter far = NewGroup("Far", min: 0f, max: 900f);

            var so = new SerializedObject(new Object[] { near, far });
            so.FindProperty("minDrawDistance").floatValue = 120f;
            so.ApplyModifiedProperties();

            StaticInstanceEditorCommon.PushDrawRangePartner(
                so.FindProperty("minDrawDistance"),
                so.FindProperty("cullDistance"),
                minEdited: true);

            var nearAfter = new SerializedObject(near);
            var farAfter = new SerializedObject(far);
            Assert.Greater(
                nearAfter.FindProperty("cullDistance").floatValue, 120f,
                "The component whose Max was passed should have been pushed.");
            Assert.AreEqual(
                900f, farAfter.FindProperty("cullDistance").floatValue, 0.001f,
                "The component whose Max was already clear must not move — its " +
                "own partner is what decides, not the first target's.");
        }

        // -- The runtime half: the belt behind the push.

        [Test]
        public void TheRuntimeIgnoresAnInvertedMin_AndSaysSoOnce()
        {
            var root = new GameObject("Inverted");
            SceneManager.MoveGameObjectToScene(root, testScene);
            var owner = new StaticInstanceOwner
            {
                Root = root,
                MinDrawDistance = 300f,
                CullDistance = 100f,
            };

            LogAssert.Expect(
                LogType.Warning,
                new System.Text.RegularExpressions.Regex(
                    @"Draw Range Min \(300\).*Max \(100\).*'Inverted'"));
            Assert.AreEqual(
                0f, StaticInstanceRendererSystem.ResolveNearLimit(owner),
                "Min must be ignored, which draws MORE than authored — the " +
                "fail-visible direction. Honouring it erases instances by size.");

            // Second rebuild, same bad pair: no second warning. LogAssert fails
            // the test on any unexpected warning, so the absence is asserted by
            // not expecting one.
            Assert.AreEqual(0f, StaticInstanceRendererSystem.ResolveNearLimit(owner));
        }

        [Test]
        public void FixingThePair_ReArmsTheWarning()
        {
            var root = new GameObject("Refixed");
            SceneManager.MoveGameObjectToScene(root, testScene);
            var owner = new StaticInstanceOwner
            {
                Root = root,
                MinDrawDistance = 300f,
                CullDistance = 100f,
            };

            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("Draw Range Min"));
            StaticInstanceRendererSystem.ResolveNearLimit(owner);

            owner.CullDistance = 900f;
            Assert.AreEqual(
                300f, StaticInstanceRendererSystem.ResolveNearLimit(owner), 0.001f,
                "A valid pair must be honoured unchanged.");

            owner.CullDistance = 100f;
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("Draw Range Min"));
            Assert.AreEqual(0f, StaticInstanceRendererSystem.ResolveNearLimit(owner));
        }

        // Equality is degenerate, not legal: a band of zero authored width,
        // which only survives at all because the radius widens it. Both the
        // push and the belt agree on that through one predicate.
        [Test]
        public void TheInvertedPredicate_TreatsEqualAsInverted_AndZeroMaxAsUnlimited()
        {
            Assert.IsTrue(StaticInstanceRendererSystem.IsDrawRangeInverted(100f, 100f));
            Assert.IsTrue(StaticInstanceRendererSystem.IsDrawRangeInverted(101f, 100f));
            Assert.IsFalse(StaticInstanceRendererSystem.IsDrawRangeInverted(99f, 100f));
            Assert.IsFalse(
                StaticInstanceRendererSystem.IsDrawRangeInverted(5000f, 0f),
                "Max = 0 is unlimited; no Min can be past it.");
            Assert.IsFalse(StaticInstanceRendererSystem.IsDrawRangeInverted(0f, 0f));
        }

        private StaticInstanceScatter NewGroup(string name, float min, float max)
        {
            var go = new GameObject(name);
            SceneManager.MoveGameObjectToScene(go, testScene);
            var group = go.AddComponent<StaticInstanceScatter>();
            var so = new SerializedObject(group);
            so.FindProperty("minDrawDistance").floatValue = min;
            so.FindProperty("cullDistance").floatValue = max;
            so.ApplyModifiedProperties();
            return group;
        }
    }
}
