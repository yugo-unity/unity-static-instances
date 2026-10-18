using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StaticInstances.Editor.Tests
{
    // SIS accepts Time as a fade mode.
    //
    // The fade itself is per-instance state inside a Burst job, so these tests pin
    // the authoring contract instead: the setter must keep the value it is given,
    // and the owner must be told which half of the mode to run.
    public sealed class StaticInstanceScatterTimeFadeTests
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

        // Time must survive the setter as authored; a setter that took Time and
        // stored Distance would make Time unreachable.
        [Test]
        public void TheSetter_KeepsTime_RatherThanClampingItToDistance()
        {
            StaticInstanceScatter scatter = NewScatter();

            scatter.FadeMode = StaticInstanceFadeMode.Time;

            Assert.AreEqual(
                StaticInstanceFadeMode.Time, scatter.FadeMode,
                "Time must be stored as authored; the setter must not coerce it to another mode.");
        }

        // EffectiveFadeMode resolves against the project's Cross Fade switch and
        // nothing else; it does not fold Time to Distance.
        [Test]
        public void TheEffectiveMode_ReportsTime_WhenCrossFadeIsOn()
        {
            StaticInstanceScatter scatter = NewScatter();
            scatter.FadeMode = StaticInstanceFadeMode.Time;

            if (!StaticInstanceLodSystem.CrossFadeActive)
            {
                Assert.AreEqual(
                    StaticInstanceFadeMode.None, scatter.EffectiveFadeMode,
                    "With Cross Fade off every mode resolves to None.");
                return;
            }

            Assert.AreEqual(
                StaticInstanceFadeMode.Time, scatter.EffectiveFadeMode,
                "The authored mode must reach the runtime unchanged.");
        }

        // Each authorable mode must survive the setter unchanged.
        [Test]
        public void EveryAuthorableMode_RoundTrips()
        {
            StaticInstanceScatter scatter = NewScatter();

            foreach (StaticInstanceFadeMode mode in new[]
                     {
                         StaticInstanceFadeMode.None,
                         StaticInstanceFadeMode.Distance,
                         StaticInstanceFadeMode.Time,
                     })
            {
                scatter.FadeMode = mode;
                Assert.AreEqual(mode, scatter.FadeMode, $"{mode} did not survive the setter.");
            }
        }

        // The draw-range half of the mode. `LodDistanceFade` is the per-owner flag
        // the eval job branches on; Time has to clear it, or the ladder would fade
        // on time while the draw limits still faded on distance.
        [Test]
        public void TheOwnerFlag_FollowsTheMode()
        {
            StaticInstanceScatter scatter = NewScatter();
            var owner = new StaticInstanceOwner();

            scatter.FadeMode = StaticInstanceFadeMode.Distance;
            InvokeApplyLodParams(scatter, owner);
            Assert.IsTrue(
                owner.LodDistanceFade,
                "Distance mode must still take the stateless band — the default path.");

            scatter.FadeMode = StaticInstanceFadeMode.Time;
            InvokeApplyLodParams(scatter, owner);
            Assert.IsFalse(
                owner.LodDistanceFade,
                "Time mode must put the owner on the time-driven path; the owner " +
                "would otherwise keep fading on distance.");
        }

        // `LodTimeFade` exists because `!LodDistanceFade` is not the same question:
        // `None` answers it identically to `Time`. The `None` row below pins that
        // difference.
        [Test]
        public void TheTimeFlag_IsTrueForTimeAlone_AndNoneIsWhereItMatters()
        {
            StaticInstanceScatter scatter = NewScatter();
            var owner = new StaticInstanceOwner();

            scatter.FadeMode = StaticInstanceFadeMode.None;
            InvokeApplyLodParams(scatter, owner);
            Assert.IsFalse(
                owner.LodTimeFade,
                "None must NOT arm the per-instance time machine — it is the " +
                "default, so the whole feature tax rides on this row.");
            Assert.IsFalse(
                owner.LodDistanceFade,
                "fixture: None answers the distance question exactly as Time " +
                "does, which is why a second flag exists.");

            scatter.FadeMode = StaticInstanceFadeMode.Distance;
            InvokeApplyLodParams(scatter, owner);
            Assert.IsFalse(owner.LodTimeFade, "Distance takes the band, not the machine.");

            scatter.FadeMode = StaticInstanceFadeMode.Time;
            InvokeApplyLodParams(scatter, owner);
            Assert.IsTrue(owner.LodTimeFade, "Time is the one mode that arms it.");
        }

        // The band width is handed over in Distance mode only — it is what
        // turns the distance band on, and a Time owner must not carry one or
        // both fades would write the same crossfade byte.
        [Test]
        public void TheBandWidth_IsHandedOver_InDistanceModeOnly()
        {
            StaticInstanceScatter scatter = NewScatter();
            scatter.CrossFadeWidth = 12f;
            var owner = new StaticInstanceOwner();

            scatter.FadeMode = StaticInstanceFadeMode.Distance;
            InvokeApplyLodParams(scatter, owner);
            Assert.AreEqual(
                12f, owner.LodCrossFadeWidth, 0.0001f,
                "fixture: Distance mode hands the authored width over.");

            scatter.FadeMode = StaticInstanceFadeMode.Time;
            InvokeApplyLodParams(scatter, owner);
            Assert.AreEqual(
                0f, owner.LodCrossFadeWidth, 0.0001f,
                "A zero width is how the band's absence is expressed internally, " +
                "and it is what turns the per-instance " +
                "hysteresis on for the time path.");
        }

        // The per-component duration. 0 is the authored "follow the project"
        // sentinel, so it must NOT be stored as a rate.
        [Test]
        public void TheDuration_FollowsTheProject_WhenLeftAtZero()
        {
            StaticInstanceScatter scatter = NewScatter();
            scatter.FadeMode = StaticInstanceFadeMode.Time;

            Assert.AreEqual(0f, scatter.LodFadeDuration, 0.0001f, "fixture: the sentinel is the default.");
            Assert.AreEqual(
                Mathf.Max(0f, StaticInstanceLodSystem.LodFadeDuration),
                scatter.EffectiveLodFadeDuration,
                0.0001f,
                "0 means follow the project knob, read live — the same " +
                "sentinel carries.");
        }

        [Test]
        public void TheDuration_Wins_WhenAuthored()
        {
            StaticInstanceScatter scatter = NewScatter();
            scatter.FadeMode = StaticInstanceFadeMode.Time;
            scatter.LodFadeDuration = 2.5f;

            Assert.AreEqual(
                2.5f, scatter.EffectiveLodFadeDuration, 0.0001f,
                "An authored duration is the point of the field.");
        }

        // The renderer's own contract: -1 = follow the live global, > 0 = that
        // rate, 0 = instant. A non-Time mode must push 0 (hard cut), or the
        // owner keeps a rate the mode no longer wants — which is why the mode
        // setter pushes the draw-range side too.
        [Test]
        public void TheOwnerRate_FollowsTheModeAndTheAuthoredValue()
        {
            StaticInstanceScatter scatter = NewScatter();
            var owner = new StaticInstanceOwner();

            scatter.FadeMode = StaticInstanceFadeMode.Time;
            Assert.AreEqual(
                -1f, PushedDuration(scatter), 0.0001f,
                "Time with the sentinel must push -1, the renderer's " +
                "'follow the live global' — never a baked-in number.");

            scatter.LodFadeDuration = 2.5f;
            Assert.AreEqual(
                2.5f, PushedDuration(scatter), 0.0001f,
                "An authored duration reaches the owner as itself.");

            scatter.FadeMode = StaticInstanceFadeMode.Distance;
            Assert.AreEqual(
                0f, PushedDuration(scatter), 0.0001f,
                "Outside Time the draw-range fade hard-cuts; a leftover rate " +
                "would animate a fade the mode does not want.");

            // The flag half of the same push, so the two cannot drift apart.
            InvokeApplyLodParams(scatter, owner);
            Assert.IsTrue(owner.LodDistanceFade, "fixture: Distance keeps the band path.");
        }

        private static float PushedDuration(StaticInstanceScatter scatter)
        {
            return (float)typeof(StaticInstanceScatter)
                .GetProperty(
                    "PushedFadeDuration",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)
                .GetValue(scatter);
        }

        private static void InvokeApplyLodParams(
            StaticInstanceScatter scatter, StaticInstanceOwner owner)
        {
            typeof(StaticInstanceScatter)
                .GetMethod(
                    "ApplyLodParams",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)
                .Invoke(scatter, new object[] { owner });
        }

        private StaticInstanceScatter NewScatter()
        {
            var go = new GameObject("Scatter");
            SceneManager.MoveGameObjectToScene(go, testScene);
            return go.AddComponent<StaticInstanceScatter>();
        }
    }
}
