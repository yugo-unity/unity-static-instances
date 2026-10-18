using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace StaticInstances.Editor.Tests
{
    // Covers build-time stripping decisions for scenes that contain SIS.
    public sealed class StaticInstanceBundleStrippingTests
    {
        private BatchRendererGroupStrippingMode savedStrippingMode;
        private Scene testScene;
        private bool testSceneOpen;

        [SetUp]
        public void SetUp()
        {
            savedStrippingMode = EditorGraphicsSettings.batchRendererGroupShaderStrippingMode;
            StaticInstanceShaderStripping.Reset();
        }

        [TearDown]
        public void TearDown()
        {
            SetStrippingMode(savedStrippingMode);
            StaticInstanceShaderStripping.Reset();
            if (testSceneOpen)
            {
                EditorSceneManager.ClosePreviewScene(testScene);
                testSceneOpen = false;
            }
        }

        // -- The violation walk: what a build script is told before the build.

        [Test]
        public void Violations_ReportTheKeepAllSetting_ForAScatterScene()
        {
            SetStrippingMode(BatchRendererGroupStrippingMode.StripAll);
            NewTestScene();
            CreateScatterOwner();

            var violations = new List<string>();
            StaticInstanceBuildScan.CollectSceneViolations(testScene, violations);

            Assert.AreEqual(1, violations.Count,
                "A Scatter scene delivered in a bundle was not reported for " +
                "BatchRendererGroup Variants — the bundle would build clean and " +
                "render nothing, and a throw from inside the build cannot catch it " +
                "because SBP swallows the exception.");
            StringAssert.Contains("Keep All", violations[0]);
        }

        [Test]
        public void Violations_AreEmpty_WhenConfiguredCorrectly()
        {
            SetStrippingMode(BatchRendererGroupStrippingMode.KeepAll);
            NewTestScene();
            CreateScatterOwner();

            var violations = new List<string>();
            StaticInstanceBuildScan.CollectSceneViolations(testScene, violations);

            Assert.IsEmpty(violations,
                "A correctly configured scene produced a violation, which would " +
                "refuse a build that should have run.");
        }

        [Test]
        public void Violations_AreEmpty_ForASceneWithNoStaticInstancesContent()
        {
            // A shader- or material-only Addressables group is a real delivery
            // and carries no Static Instances content: nothing to guard, nothing
            // to refuse, whatever the setting says.
            SetStrippingMode(BatchRendererGroupStrippingMode.StripAll);
            NewTestScene();
            var plain = new GameObject("PlainRoot");
            SceneManager.MoveGameObjectToScene(plain, testScene);

            var violations = new List<string>();
            StaticInstanceBuildScan.CollectSceneViolations(testScene, violations);

            Assert.IsEmpty(violations,
                "A scene with no Static Instances content was refused over a " +
                "setting that cannot affect it.");
        }

        [Test]
        public void Violations_AreReportedOnce_ForTwoOwnersInOneScene()
        {
            SetStrippingMode(BatchRendererGroupStrippingMode.StripAll);
            NewTestScene();
            CreateScatterOwner();
            CreateScatterOwner();

            var violations = new List<string>();
            StaticInstanceBuildScan.CollectSceneViolations(testScene, violations);

            Assert.AreEqual(1, violations.Count,
                "The project-wide setting was reported per owner. A build refusal " +
                "listing the same sentence once per component reads as many " +
                "problems where there is one.");
        }

        [Test]
        public void Violations_RejectFreshBakedNestedScatterOwners()
        {
            SetStrippingMode(BatchRendererGroupStrippingMode.KeepAll);
            NewTestScene();

            var parent = new GameObject("OuterScatter");
            SceneManager.MoveGameObjectToScene(parent, testScene);
            var outer = parent.AddComponent<StaticInstanceScatter>();
            AddRenderer(parent.transform);

            var child = new GameObject("NestedScatter");
            child.transform.SetParent(parent.transform, false);
            var nested = child.AddComponent<StaticInstanceScatter>();
            AddRenderer(child.transform);

            Assert.IsTrue(StaticInstanceBakeUtility.BakeGroup(outer));
            Assert.IsTrue(StaticInstanceBakeUtility.BakeGroup(nested));
            Assert.IsFalse(StaticInstanceBakeUtility.IsBakeStale(outer));
            Assert.IsFalse(StaticInstanceBakeUtility.IsBakeStale(nested));

            var violations = new List<string>();
            StaticInstanceBuildScan.CollectTargetViolations(testScene, outer, violations);

            Assert.AreEqual(1, violations.Count);
            Assert.IsTrue(
                violations.Exists(message => message.Contains("Nested SIS owners are not supported")),
                "A fresh bake must still be refused when SIS owners are nested. " +
                "Staleness is unrelated to this unsupported hierarchy.");
        }

        // -- Arming and teardown: the state machine the two entry points own.

        [Test]
        public void Begin_OnADeliveryWithNoScenes_AllowsTheBuild()
        {
            SetStrippingMode(BatchRendererGroupStrippingMode.StripAll);

            Assert.IsNull(
                StaticInstanceBundleStripping.BeginContentBuild(new string[0]),
                "A delivery with no scenes was refused.");
        }

        [Test]
        public void Begin_IsAFullReset_SoStateNeverCrossesOperations()
        {
            SetStrippingMode(BatchRendererGroupStrippingMode.KeepAll);
            // Pretend a previous operation armed and collected.
            StaticInstanceShaderStripping.Active = true;
            StaticInstanceShaderStripping.AddShader(
                Shader.Find("Universal Render Pipeline/Lit"), canFailBuild: true);
            Assert.IsNotEmpty(StaticInstanceShaderStripping.KeptShaders);

            StaticInstanceBundleStripping.BeginContentBuild(new string[0]);

            Assert.IsEmpty(
                StaticInstanceShaderStripping.KeptShaders,
                "A keep-set survived into the next build operation. It describes " +
                "the content of the build that produced it, so carrying it over is " +
                "how a build strips a shader it never saw.");
        }

        [Test]
        public void End_LeavesTheStripperUnarmed()
        {
            SetStrippingMode(BatchRendererGroupStrippingMode.KeepAll);
            StaticInstanceShaderStripping.Active = true;
            StaticInstanceShaderStripping.AddShader(
                Shader.Find("Universal Render Pipeline/Lit"), canFailBuild: true);

            StaticInstanceBundleStripping.EndContentBuild();

            Assert.IsFalse(
                StaticInstanceShaderStripping.Active,
                "End left the stripper armed. Unarmed is the resting state on " +
                "purpose: a bundled build has no build-end callback, so if End " +
                "does not clear it, nothing will.");
            Assert.IsEmpty(StaticInstanceShaderStripping.KeptShaders);
        }

        [Test]
        public void HintProbe_IsRegistered_WhenTheAddressablesIntegrationIsPresent()
        {
            bool? answer = null;
            Assert.DoesNotThrow(
                () => answer = StaticInstanceBundleStripping.NothingToActOn);
            if (!answer.HasValue)
                Assert.Ignore("Addressables is not installed; run this check in the optional integration lane.");
        }

        // -- The Graphics-page hint: shown only where it is true AND actionable.

        [Test]
        public void StrippingDrawer_ShowsTheBuilderHint_OnlyWhenThePackageIsPresentAndSomethingIsActionable()
        {
            if (!GraphicsSettings.TryGetRenderPipelineSettings(
                    out StaticInstanceStrippingSettings live))
            {
                Assert.Ignore("No URP global settings container in this project.");
            }

            bool savedEnabled = live.stripUnusedDotsInstancingVariants;
            System.Func<bool> savedProbe = StaticInstanceBundleStripping.HintProbe;
            var container = GraphicsSettings.GetSettingsForRenderPipeline<
                UnityEngine.Rendering.Universal.UniversalRenderPipeline>();
            var so = new SerializedObject(container);
            SerializedProperty settings = FindStrippingProperty(so);
            Assert.IsNotNull(settings, "could not locate the stripping settings property");
            var drawer = new StaticInstanceStrippingSettingsDrawer();

            try
            {
                live.stripUnusedDotsInstancingVariants = true;
                so.Update();

                StaticInstanceBundleStripping.HintProbe = null;
                float packageAbsent = drawer.GetPropertyHeight(settings, GUIContent.none);

                StaticInstanceBundleStripping.HintProbe = () => true;
                float nothingToActOn = drawer.GetPropertyHeight(settings, GUIContent.none);

                StaticInstanceBundleStripping.HintProbe = () => false;
                float hintWarranted = drawer.GetPropertyHeight(settings, GUIContent.none);

                Assert.AreEqual(packageAbsent, nothingToActOn,
                    "A project WITHOUT the package and a project with nothing to " +
                    "act on must look the same — an integrated build script, or no " +
                    "Addressables settings at all, are both nothing to act on.");
                Assert.Greater(hintWarranted, nothingToActOn + EditorGUIUtility.singleLineHeight,
                    "The hint did not appear in the one state it exists for: the " +
                    "package present and something to act on.");

                // And it must not appear at all while the option it sits under
                // is off: nothing about it would be actionable there.
                live.stripUnusedDotsInstancingVariants = false;
                so.Update();
                StaticInstanceBundleStripping.HintProbe = () => false;
                float strippingOff = drawer.GetPropertyHeight(settings, GUIContent.none);
                StaticInstanceBundleStripping.HintProbe = () => true;
                Assert.AreEqual(
                    strippingOff, drawer.GetPropertyHeight(settings, GUIContent.none),
                    "The hint showed while variant stripping was disabled.");
            }
            finally
            {
                live.stripUnusedDotsInstancingVariants = savedEnabled;
                StaticInstanceBundleStripping.HintProbe = savedProbe;
            }
        }

        // -- helpers

        // The container is a managed reference inside the global settings
        // asset, so it is found by walking rather than by a fixed path.
        private static SerializedProperty FindStrippingProperty(SerializedObject so)
        {
            SerializedProperty it = so.GetIterator();
            while (it.NextVisible(true))
            {
                if (it.propertyType == SerializedPropertyType.ManagedReference &&
                    it.managedReferenceValue is StaticInstanceStrippingSettings)
                {
                    return it.Copy();
                }
            }

            return null;
        }

        private void NewTestScene()
        {
            testScene = EditorSceneManager.NewPreviewScene();
            testSceneOpen = true;
        }

        private void CreateScatterOwner()
        {
            var owner = new GameObject("ScatterOwner");
            SceneManager.MoveGameObjectToScene(owner, testScene);
            owner.AddComponent<StaticInstanceScatter>();

            var child = new GameObject("Renderer");
            child.transform.SetParent(owner.transform, false);
            child.AddComponent<MeshFilter>().sharedMesh =
                Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            child.AddComponent<MeshRenderer>().sharedMaterial =
                new Material(Shader.Find("Universal Render Pipeline/Lit"));
        }

        private static void AddRenderer(Transform parent)
        {
            var child = new GameObject("Renderer");
            child.transform.SetParent(parent, false);
            child.AddComponent<MeshFilter>().sharedMesh =
                Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            child.AddComponent<MeshRenderer>().sharedMaterial =
                new Material(Shader.Find("Universal Render Pipeline/Lit"));
        }

        private static void SetStrippingMode(BatchRendererGroupStrippingMode mode)
        {
            var so = new SerializedObject(
                AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset")[0]);
            so.FindProperty("m_BrgStripping").enumValueIndex = (int)mode;
            so.ApplyModifiedPropertiesWithoutUndo();
            Assert.AreEqual(
                mode,
                EditorGraphicsSettings.batchRendererGroupShaderStrippingMode,
                "The harness could not set BatchRendererGroup Variants — the " +
                "serialized field behind it has probably been renamed.");
        }
    }
}
