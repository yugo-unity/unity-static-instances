using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using UnityEngine;

namespace StaticInstances.Editor.Tests
{
    // A wind-stripped clone is a material variant selection, so its whole
    // correctness is "which keywords does the clone turn off". Two ways it
    // can be wrong without anything complaining: strip a keyword the shader never
    // declared (an error in the console at best, a wrong variant at worst), or
    // fail to strip one that is on, in which case the clone costs a material and
    // buys nothing.
    public sealed class StaticInstanceWindStaticMaterialTests
    {
        private static Shader SpeedTree()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Nature/SpeedTree9_URP");
            if (shader == null)
                Assert.Ignore("URP's SpeedTree9 shader is not present in this project");
            return shader;
        }

        [Test]
        public void EnabledWindKeywords_ReportsOnlyTheOnesTheMaterialActuallyEnables()
        {
            var material = new Material(SpeedTree());
            try
            {
                foreach (string keyword in StaticInstanceSpeedTreeWind.Keywords)
                    material.DisableKeyword(keyword);
                Assert.IsEmpty(StaticInstanceWindStaticMaterials.EnabledWindKeywords(material));

                material.EnableKeyword("_WIND_SHARED");
                material.EnableKeyword("_WIND_RIPPLE");

                List<string> enabled =
                    StaticInstanceWindStaticMaterials.EnabledWindKeywords(material);

                CollectionAssert.AreEquivalent(
                    new[] { "_WIND_SHARED", "_WIND_RIPPLE" }, enabled,
                    "the clone must strip the keywords the material declares and enables; " +
                    "a fixed list of all five would write keywords the shader does not declare");
            }
            finally
            {
                Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void EnabledWindKeywords_IsEmptyForAShaderWithNoWindKeywords()
        {
            Shader lit = Shader.Find("Universal Render Pipeline/Lit");
            if (lit == null)
                Assert.Ignore("URP Lit is not present in this project");

            var material = new Material(lit);
            try
            {
                Assert.IsEmpty(StaticInstanceWindStaticMaterials.EnabledWindKeywords(material));
            }
            finally
            {
                Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void EnsureClone_ReturnsNothingForAMaterialWithNoWindToStrip()
        {
            var material = new Material(SpeedTree());
            try
            {
                foreach (string keyword in StaticInstanceSpeedTreeWind.Keywords)
                    material.DisableKeyword(keyword);

                Assert.IsNull(StaticInstanceWindStaticMaterials.EnsureClone(material),
                    "already-static content needs no clone: such a material is already still");
            }
            finally
            {
                Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void EnsureClone_ReturnsNothingForNull()
        {
            Assert.IsNull(StaticInstanceWindStaticMaterials.EnsureClone(null));
        }

        [Test]
        public void ClonePathFor_IsNullForAnUnsavedMaterial()
        {
            var material = new Material(SpeedTree());
            try
            {
                // An in-memory material has no GUID, so there is no deterministic
                // name to give its clone. Declining beats inventing one.
                Assert.IsNull(StaticInstanceWindStaticMaterials.ClonePathFor(material));
            }
            finally
            {
                Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void ClonePathFor_CarriesTheSourceIdentity_SoTwoSubAssetsCannotCollide()
        {
            // SpeedTree materials are embedded sub-assets: several share one GUID,
            // and two .st9 files in different folders can hold identically named
            // ones. The generated file name has to separate them, or one species
            // silently draws with another's material.
            Shader shader = SpeedTree();
            string path = "Assets/SIS_WindStaticTest_Source.mat";
            var source = new Material(shader) { name = "Tree_Mat" };
            try
            {
                AssetDatabase.CreateAsset(source, path);
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                    source, out string guid, out long localId);

                string clonePath = StaticInstanceWindStaticMaterials.ClonePathFor(source);

                Assert.IsNotNull(clonePath);
                StringAssert.Contains(guid, clonePath);
                StringAssert.Contains(localId.ToString(), clonePath);
                StringAssert.StartsWith("Assets/SIS_WindStatic/", clonePath);
                StringAssert.EndsWith(".mat", clonePath);
            }
            finally
            {
                AssetDatabase.DeleteAsset(path);
            }
        }

        [Test]
        public void EnsureClone_WritesAnAssetWithTheWindKeywordsOff_AndIsIdempotent()
        {
            Shader shader = SpeedTree();
            string sourcePath = "Assets/SIS_WindStaticTest_Source.mat";
            var source = new Material(shader) { name = "Tree_Mat" };
            string clonePath = null;
            try
            {
                AssetDatabase.CreateAsset(source, sourcePath);
                // Both halves, because that is the state a real imported SpeedTree
                // material is in: the ShaderGraph boolean keyword is backed by a
                // float property of the same name.
                source.SetFloat("_WIND_SHARED", 1f);
                source.EnableKeyword("_WIND_SHARED");
                EditorUtility.SetDirty(source);

                Material clone = StaticInstanceWindStaticMaterials.EnsureClone(source);
                clonePath = AssetDatabase.GetAssetPath(clone);

                Assert.IsNotNull(clone);
                Assert.IsNotEmpty(clonePath,
                    "the clone must be a serialized asset — the wind keywords are " +
                    "shader_feature, so a runtime clone's stripped variant would never be " +
                    "compiled into the build");
                Assert.AreEqual(shader, clone.shader,
                    "same shader, different keywords — this is a variant selection, not a " +
                    "different material");

                // Assert after a WRITE AND RELOAD, not on the in-memory instance.
                // Clearing the keyword without clearing its backing property looks
                // correct in memory and comes back enabled at the next import —
                // which is exactly the bug this shape of test exists to catch, and
                // an in-memory assertion passes straight through it.
                AssetDatabase.SaveAssets();
                AssetDatabase.ImportAsset(clonePath, ImportAssetOptions.ForceUpdate);
                var reloaded = AssetDatabase.LoadAssetAtPath<Material>(clonePath);

                Assert.IsFalse(reloaded.IsKeywordEnabled("_WIND_SHARED"),
                    "the stripped variant must still be stripped after a round trip");
                Assert.AreEqual(0f, reloaded.GetFloat("_WIND_SHARED"),
                    "the backing property drives the keyword through validation — leaving it " +
                    "at 1 lets validation turn the keyword back on");

                // Second pass over an already-correct clone: same asset, unchanged.
                Material again = StaticInstanceWindStaticMaterials.EnsureClone(source);
                Assert.AreEqual(clonePath, AssetDatabase.GetAssetPath(again));
                Assert.IsFalse(again.IsKeywordEnabled("_WIND_SHARED"));
            }
            finally
            {
                if (!string.IsNullOrEmpty(clonePath))
                    AssetDatabase.DeleteAsset(clonePath);
                AssetDatabase.DeleteAsset(sourcePath);
            }
        }

        [Test]
        public void EnsureClone_RefreshesAStaleCloneInPlace_KeepingItsGuid()
        {
            Shader shader = SpeedTree();
            string sourcePath = "Assets/SIS_WindStaticTest_Source.mat";
            var source = new Material(shader) { name = "Tree_Mat" };
            string clonePath = null;
            try
            {
                AssetDatabase.CreateAsset(source, sourcePath);
                source.SetFloat("_WIND_SHARED", 1f);
                source.EnableKeyword("_WIND_SHARED");
                EditorUtility.SetDirty(source);

                Material clone = StaticInstanceWindStaticMaterials.EnsureClone(source);
                clonePath = AssetDatabase.GetAssetPath(clone);
                string guidBefore = AssetDatabase.AssetPathToGUID(clonePath);

                // Something re-enabled the keyword on the clone — a hand edit, or a
                // CopySerialized that carried the source's state across.
                clone.SetFloat("_WIND_SHARED", 1f);
                clone.EnableKeyword("_WIND_SHARED");
                EditorUtility.SetDirty(clone);

                Material refreshed = StaticInstanceWindStaticMaterials.EnsureClone(source);

                Assert.IsFalse(refreshed.IsKeywordEnabled("_WIND_SHARED"),
                    "a stale clone must be corrected, or the draw group keeps paying " +
                    "the wind vertex cost");
                Assert.AreEqual(0f, refreshed.GetFloat("_WIND_SHARED"),
                    "and the backing property with it, or validation puts the keyword back");
                Assert.AreEqual(guidBefore, AssetDatabase.AssetPathToGUID(clonePath),
                    "the refresh has to happen in place — a new GUID would orphan every " +
                    "draw group already referencing the clone");
            }
            finally
            {
                if (!string.IsNullOrEmpty(clonePath))
                    AssetDatabase.DeleteAsset(clonePath);
                AssetDatabase.DeleteAsset(sourcePath);
            }
        }

        [Test]
        public void ADrawGroupWithoutAClone_ReadsAsHavingNoStaticVariant()
        {
            // A draw group baked without a clone (for example by a build-time bake, which
            // never generates assets) has a null StaticMaterial. Null means fall back to
            // the original material, not draw nothing.
            var group = default(BakedDrawGroup);
            Assert.IsNull(group.StaticMaterial);
        }

        [Test]
        public void SceneGenerationReplacesOnlyTheSelectedScenesCloneLedgerRow()
        {
            string folder = "Assets/SIWindSceneScope_" + System.Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", System.IO.Path.GetFileName(folder));
            string previousRegistry = StaticInstanceWindCloneRegistry.RegistryPathOverride;
            Scene first = default;
            Scene second = default;
            try
            {
                StaticInstanceWindCloneRegistry.RegistryPathOverride = folder + "/Ledger.asset";
                first = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                Assert.IsTrue(EditorSceneManager.SaveScene(first, folder + "/First.unity"));
                second = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                Assert.IsTrue(EditorSceneManager.SaveScene(second, folder + "/Second.unity"));
                string firstGuid = AssetDatabase.AssetPathToGUID(first.path);
                string secondGuid = AssetDatabase.AssetPathToGUID(second.path);
                StaticInstanceWindCloneRegistry.Record(firstGuid, first.name,
                    new Dictionary<string, string> { { "Assets/FirstClone.mat", "first" } });
                StaticInstanceWindCloneRegistry.Record(secondGuid, second.name,
                    new Dictionary<string, string> { { "Assets/SecondClone.mat", "second" } });
                Assert.AreEqual(0, StaticInstanceWindStaticMaterials.GenerateForScene(first));
                var ledger = AssetDatabase.LoadAssetAtPath<StaticInstanceWindCloneLedger>(
                    StaticInstanceWindCloneRegistry.RegistryPathOverride);
                Assert.IsEmpty(ledger.Rows.Find(row => row.SceneGuid == firstGuid).Clones);
                Assert.AreEqual("Assets/SecondClone.mat",
                    ledger.Rows.Find(row => row.SceneGuid == secondGuid).Clones[0].Path);
            }
            finally
            {
                StaticInstanceWindCloneRegistry.RegistryPathOverride = previousRegistry;
                if (second.IsValid() && second.isLoaded)
                    EditorSceneManager.CloseScene(second, true);
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                AssetDatabase.DeleteAsset(folder);
            }
        }
    }
}
