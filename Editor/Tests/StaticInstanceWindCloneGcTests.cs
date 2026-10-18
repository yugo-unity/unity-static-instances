using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace StaticInstances.Editor.Tests
{
    // The wind-stripped clone GC.
    //
    // What these pin is the reason the design uses a registry rather than "delete
    // this scene's clones": clone paths are deterministic from the source, so two scenes
    // that use the same species point at the SAME asset, and a per-scene delete would
    // break the other scene's serialized reference into the silent null fallback. That
    // failure is invisible — a still tree — so it has to be caught here.
    //
    // Every test runs against a TEMP registry file. That is not tidiness: the registry is
    // a real file in the host project, and a test on the default path would delete whatever
    // had been recorded.
    public sealed class StaticInstanceWindCloneGcTests
    {
        private const string TempFolder = "Assets/_CloneGcTests";

        // Fixtures placed in the developer's real clone folder carry this prefix so
        // TearDown can remove exactly those and nothing else. No generated clone can
        // collide: `ClonePathFor` always emits "SISW_{guid}_{localId}".
        private const string FixturePrefix = "__GcTestFixture_";

        private string registryPath;
        private UnityEngine.SceneManagement.Scene previewScene;
        private bool previewSceneOpen;

        [SetUp]
        public void SetUp()
        {
            if (!AssetDatabase.IsValidFolder(TempFolder))
                AssetDatabase.CreateFolder("Assets", "_CloneGcTests");
            // Under `Assets/`, because the ledger is an ASSET: an OS temp path cannot
            // hold one. It is still separate from the real ledger, which is the
            // developer's own and must not be touched.
            registryPath = $"{TempFolder}/SISW_TestLedger.asset";
            StaticInstanceWindCloneRegistry.RegistryPathOverride = registryPath;
        }

        [TearDown]
        public void TearDown()
        {
            StaticInstanceWindCloneRegistry.RegistryPathOverride = null;
            if (previewSceneOpen)
            {
                UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(previewScene);
                previewSceneOpen = false;
            }
            // The ledger asset goes with the folder it lives in.
            if (AssetDatabase.IsValidFolder(TempFolder))
                AssetDatabase.DeleteAsset(TempFolder);

            // Only this suite's own fixtures, matched by prefix — the folder belongs to
            // the developer and may hold real generated clones.
            string cloneFolder = StaticInstanceWindStaticMaterials.OutputFolder;
            if (AssetDatabase.IsValidFolder(cloneFolder))
            {
                foreach (string guid in AssetDatabase.FindAssets(
                             FixturePrefix, new[] { cloneFolder }))
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    if (!string.IsNullOrEmpty(path) &&
                        Path.GetFileName(path).StartsWith(FixturePrefix))
                    {
                        AssetDatabase.DeleteAsset(path);
                    }
                }
            }
        }

        [Test]
        public void ACloneTwoScenesShare_SurvivesClearingOneOfThem()
        {
            // The whole reason the registry exists. Clone paths are deterministic from the
            // source, so this is the ordinary case on any project with two scenes holding
            // the same tree species — not a corner.
            string shared = CreateMaterial("Shared");
            StaticInstanceWindCloneRegistry.Record("scene-a", "SceneA", Clones(shared));
            StaticInstanceWindCloneRegistry.Record("scene-b", "SceneB", Clones(shared));

            List<string> deleted = StaticInstanceWindCloneRegistry.CollectForClearedScene("scene-a", null);

            CollectionAssert.IsEmpty(deleted,
                "a clone another scene's row still claims must not be collected");
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Material>(shared),
                "the asset must still exist — the other scene's bake data references it, " +
                "and a missing reference degrades silently to a still tree");
        }

        [Test]
        public void ACloneOnlyTheClearedSceneClaimed_IsCollected()
        {
            // The control for the test above. Without it, a GC that deleted nothing at all
            // would pass.
            string onlyMine = CreateMaterial("OnlyMine");
            StaticInstanceWindCloneRegistry.Record("scene-a", "SceneA", Clones(onlyMine));

            List<string> deleted = StaticInstanceWindCloneRegistry.CollectForClearedScene("scene-a", null);

            CollectionAssert.AreEqual(new[] { onlyMine }, deleted);
            Assert.IsNull(AssetDatabase.LoadAssetAtPath<Material>(onlyMine),
                "a clone nothing else claims is what the GC exists to remove");
        }

        [Test]
        public void TheClearedScenesOwnReferences_DoNotVetoItsOwnClones()
        {
            // The dependency check reads the asset ON DISK, so the scene being cleared
            // still references its clones at the moment of the press; treating that as a
            // blocker would make the GC collect NOTHING through its own trigger. The
            // excluded-referrer parameter handles this, and this pins the exclusion:
            // ignored, the clone goes. The opposite direction — not ignored, it stays — is
            // `AnAssetStillReferencingTheClone_VetoesTheDeletion_...`, with its OWN fixture
            // rather than this one.
            string clone = CreateCloneFolderMaterial("SelfReferenced");
            string referrer = CreateReferencingPrefab("SelfHolder", clone);
            StaticInstanceWindCloneRegistry.Record("scene-a", "SceneA", Clones(clone));

            List<string> deleted =
                StaticInstanceWindCloneRegistry.CollectForClearedScene("scene-a", referrer);

            CollectionAssert.AreEqual(new[] { clone }, deleted,
                "the referrer being cleared must not veto its own clones, or the GC can " +
                "never collect anything through the Clear action");
        }

        [Test]
        public void ACloneKeptByTheVeto_BecomesAnOrphan_SoALaterClearCanRetryIt()
        {
            // Dropping the row for a clone the veto kept would make it UNKNOWN, and unknown
            // means keep forever by design, so retiring the blocking asset could never make
            // the clone collectable again. Both halves are asserted: it is still known after
            // the press, and once the blocker is gone a second press collects it.
            string clone = CreateCloneFolderMaterial("Orphaned");
            string blocker = CreateReferencingPrefab("Blocker", clone);
            StaticInstanceWindCloneRegistry.Record("scene-a", "SceneA", Clones(clone));

            LogAssert.Expect(LogType.Log, new System.Text.RegularExpressions.Regex("Orphaned.mat"));
            CollectionAssert.IsEmpty(
                StaticInstanceWindCloneRegistry.CollectForClearedScene("scene-a", null));
            CollectionAssert.Contains(StaticInstanceWindCloneRegistry.KnownClones(), clone,
                "a clone the veto kept must stay KNOWN, or rule 1 protects it for good");

            AssetDatabase.DeleteAsset(blocker);

            List<string> deleted =
                StaticInstanceWindCloneRegistry.CollectForClearedScene("scene-b", null);
            CollectionAssert.AreEqual(new[] { clone }, deleted,
                "with the blocker gone, the next Clear must reconsider the orphan");
        }

        [Test]
        public void ACloneNoRowEverClaimed_IsNeverTouched()
        {
            // A clone with no ledger row is unknown, and unknown means keep. This
            // also makes losing the ledger (a deleted clone folder, a fresh
            // checkout) harmless.
            // The fixture lives in the REAL clone folder, not a temp one, so a
            // folder-sweeping GC would find and delete it and fail this test and the
            // folder-sweep test below.
            string stranger = CreateCloneFolderMaterial("Stranger");
            StaticInstanceWindCloneRegistry.Record("scene-a", "SceneA", Clones());

            List<string> deleted = StaticInstanceWindCloneRegistry.CollectForClearedScene("scene-a", null);

            CollectionAssert.IsEmpty(deleted);
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Material>(stranger),
                "a clone no row claims is invisible to the GC by design");
            CollectionAssert.DoesNotContain(
                StaticInstanceWindCloneRegistry.KnownClones(), stranger);
        }

        [Test]
        public void ClearingASceneWithNoRow_CollectsNothing_RatherThanSweepingTheFolder()
        {
            // The tempting shortcut is "no row? then look at the folder instead". That is
            // the unsafe sweep the design refuses, so this pins the refusal — with the
            // fixture in the folder a sweep would actually sweep.
            string untracked = CreateCloneFolderMaterial("Untracked");

            List<string> deleted =
                StaticInstanceWindCloneRegistry.CollectForClearedScene("never-recorded", null);

            CollectionAssert.IsEmpty(deleted);
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Material>(untracked));
        }

        [Test]
        public void RecordingASceneAgain_ReplacesItsRow_SoADroppedSpeciesStopsBeingKeptAlive()
        {
            // A scene that stopped using a species must stop keeping its clone alive,
            // which is only true if the row is REPLACED rather than merged. Both
            // directions asserted: the dropped one goes, the kept one stays.
            string dropped = CreateMaterial("Dropped");
            string kept = CreateMaterial("Kept");
            StaticInstanceWindCloneRegistry.Record("scene-a", "SceneA", Clones(dropped, kept));
            StaticInstanceWindCloneRegistry.Record("scene-a", "SceneA", Clones(kept));

            List<string> known = StaticInstanceWindCloneRegistry.KnownClones();
            CollectionAssert.Contains(known, kept);
            CollectionAssert.DoesNotContain(known, dropped,
                "re-recording must replace the row, or a species removed from a scene " +
                "keeps its clone alive forever");
        }

        [Test]
        public void AMalformedRegistry_FailsSafe_ByCollectingNothing()
        {
            // Failing to read must mean "no candidates", not "no constraints" — a GC acting
            // on half a record would delete assets a scene needs.
            //
            // "Malformed" here is something occupying the ledger's path that does not
            // load as a ledger, which must NOT be papered over by writing a fresh ledger
            // on top — replacing it would forget rows a scene may still depend on. So:
            // record a row, then put a Material where the ledger lives.
            string material = CreateMaterial("Safe");
            StaticInstanceWindCloneRegistry.Record("scene-a", "SceneA", Clones(material));
            AssetDatabase.DeleteAsset(registryPath);
            AssetDatabase.CreateAsset(
                new Material(Shader.Find("Universal Render Pipeline/Lit")), registryPath);

            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(
                "is not a readable clone ledger"));

            List<string> deleted = StaticInstanceWindCloneRegistry.CollectForClearedScene("scene-a", null);

            CollectionAssert.IsEmpty(deleted);
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Material>(material),
                "an unreadable registry must collect nothing rather than guess");
        }

        [Test]
        public void AnAssetStillReferencingTheClone_VetoesTheDeletion_EvenWithNoRowClaimingIt()
        {
            // Second safeguard: the 'only registry-known clones are deleted' rule
            // does not cover this case. The clone is known (scene-a's row claims it)
            // while a second, unrecorded asset also references it. The registry rule
            // alone would delete it; the live dependency check keeps it.
            //
            // A PREFAB is the referencing asset rather than a scene, for two reasons: a
            // preview scene (the safe way to make a test scene) cannot be saved as an
            // asset, and prefab-held bake data is a real supported case, so the veto has
            // to cover it.
            string clone = CreateMaterial("Referenced");
            string prefabPath = CreateReferencingPrefab("Holder", clone);
            StaticInstanceWindCloneRegistry.Record("scene-a", "SceneA", Clones(clone));

            LogAssert.Expect(LogType.Log, new System.Text.RegularExpressions.Regex(
                "keeping 'Referenced.mat'"));

            List<string> deleted = StaticInstanceWindCloneRegistry.CollectForClearedScene("scene-a", null);

            CollectionAssert.IsEmpty(deleted,
                "an asset outside the registry still references this clone, so deleting " +
                "it would break that reference silently");
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Material>(clone));
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath),
                "sanity: the referencing prefab exists, or the test proves nothing");
        }

        // A prefab whose renderer uses `materialPath`, so the asset database records a
        // dependency the GC must honour.
        private static string CreateReferencingPrefab(string name, string materialPath)
        {
            var go = new GameObject(name);
            try
            {
                go.AddComponent<MeshRenderer>().sharedMaterial =
                    AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                string path = $"{TempFolder}/{name}.prefab";
                PrefabUtility.SaveAsPrefabAsset(go, path);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                return path;
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void AnUnsavedSceneStillPointingAtTheClone_CountsAsAReferrer()
        {
            // A clone referenced only by a loaded-but-DIRTY scene is invisible to both
            // safeguards — no row claims it (the bake re-attaches an existing clone without
            // recording one) and nothing on disk mentions it (the dependency check reads
            // the imported asset). With the Clear scoped to the active scene, that is
            // reachable with two scenes loaded, which is normal during editing.
            //
            // The live-reference scan is exercised through its seam rather than through
            // `SceneManager`, because building a real loaded scene from a test means
            // dirtying the developer's open scene. Both directions are here: the owner
            // pointing at the clone blocks it, an owner pointing at something else
            // does not.
            string clone = CreateCloneFolderMaterial("LiveReferenced");
            string other = CreateCloneFolderMaterial("Unrelated");
            var wanted = new HashSet<string> { clone };

            var blockers = new Dictionary<string, string>();
            StaticInstanceWindCloneRegistry.AddLiveBlockers(
                new[] { ScatterPointingAt(clone) }, "DirtyScene", wanted, blockers);
            Assert.IsTrue(blockers.ContainsKey(clone),
                "live bake data pointing at the clone must block its deletion");
            Assert.AreEqual("DirtyScene", blockers[clone]);

            var noBlockers = new Dictionary<string, string>();
            StaticInstanceWindCloneRegistry.AddLiveBlockers(
                new[] { ScatterPointingAt(other) }, "DirtyScene", wanted, noBlockers);
            CollectionAssert.IsEmpty(noBlockers,
                "an owner pointing at a different material must not block — otherwise the " +
                "scan would veto everything and the GC would never collect");
        }

        // A Scatter whose baked draw group points at `materialPath`, built in a PREVIEW
        // scene: the safe way to make test objects, because it leaves the developer's
        // open scene clean.
        private StaticInstanceScatter ScatterPointingAt(string materialPath)
        {
            if (!previewSceneOpen)
            {
                previewScene = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
                previewSceneOpen = true;
            }

            var go = new GameObject("Owner");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, previewScene);
            var scatter = go.AddComponent<StaticInstanceScatter>();
            scatter.ApplyBakedData(
                new[] { Matrix4x4.identity },
                new[] { new Bounds(Vector3.zero, Vector3.one) },
                new[]
                {
                    new BakedDrawGroup
                    {
                        StaticMaterial = AssetDatabase.LoadAssetAtPath<Material>(materialPath),
                    },
                },
                new[] { 0 });
            return scatter;
        }

        [Test]
        public void TheCloneNameCarriesTheIdentityAndNotTheSourceName()
        {
            // Two properties: the name must be the identity (so two same-named
            // sub-assets cannot collide), and it must NOT contain the source material's
            // name (so renaming the source does not orphan the clone).
            var material = new Material(Shader.Find("Universal Render Pipeline/Lit"))
            {
                name = "A Name With Spaces/Slashes",
            };
            string path = $"{TempFolder}/NamedSource.mat";
            AssetDatabase.CreateAsset(material, path);
            Material source = AssetDatabase.LoadAssetAtPath<Material>(path);

            string clonePath = StaticInstanceWindStaticMaterials.ClonePathFor(source);

            Assert.IsNotNull(clonePath);
            string fileName = Path.GetFileName(clonePath);
            Assert.IsTrue(fileName.StartsWith("SISW_"),
                $"the clone name must carry the owner prefix; got '{fileName}'");
            Assert.IsFalse(fileName.Contains("Name"),
                "the source material's name must not appear — a name containing the source " +
                $"name would make the path unstable under a rename; got '{fileName}'");
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(source, out string guid, out long id);
            Assert.AreEqual($"SISW_{guid}_{id}.mat", fileName,
                "the whole name is the identity: prefix, guid, decimal localFileId");
        }

        [Test]
        public void TheLedgerKeepsTheSourceNameForClonesItRecords()
        {
            // The ledger stores each clone's source name as display metadata, since the
            // clone's filename does not carry it. Pinned through the asset because being
            // readable in the Inspector is the point.
            string clone = CreateMaterial("WithSource");
            StaticInstanceWindCloneRegistry.Record(
                "scene-a", "SceneA",
                new Dictionary<string, string> { [clone] = "BigTree_01" });

            var ledger = AssetDatabase.LoadAssetAtPath<StaticInstanceWindCloneLedger>(registryPath);
            Assert.IsNotNull(ledger, "the ledger must be an asset at the recorded path");
            StaticInstanceWindCloneLedger.SceneRow row =
                ledger.Rows.Find(r => r.SceneGuid == "scene-a");
            Assert.IsNotNull(row);
            Assert.AreEqual("SceneA", row.SceneName, "the scene name rides in the row too");
            Assert.AreEqual("BigTree_01", row.Clones.Find(c => c.Path == clone)?.SourceName);
        }

        // Clone paths as the ledger takes them: path → the source material's name, which
        // it carries because the clone's filename does not include the source name.
        // The tests never assert on the display name, so a marker value keeps the call
        // sites readable while still exercising the pair shape rather than a bare path.
        private static Dictionary<string, string> Clones(params string[] paths)
        {
            var map = new Dictionary<string, string>();
            foreach (string path in paths)
                map[path] = "SourceOf_" + Path.GetFileNameWithoutExtension(path);
            return map;
        }

        private static string CreateMaterial(string name) =>
            CreateMaterialAt(TempFolder, name);

        // A fixture in the REAL clone folder, so a mutation that swept that folder would
        // delete it and fail the test that claims to forbid sweeping. Named with a prefix
        // no generated clone can produce (`ClonePathFor` always emits
        // "SISW_{guid}_{localId}"), and removed in TearDown — the folder is the
        // developer's own and may hold real clones, which are never touched.
        private static string CreateCloneFolderMaterial(string name)
        {
            if (!AssetDatabase.IsValidFolder(StaticInstanceWindStaticMaterials.OutputFolder))
                AssetDatabase.CreateFolder("Assets", "SIS_WindStatic");
            return CreateMaterialAt(
                StaticInstanceWindStaticMaterials.OutputFolder, $"{FixturePrefix}{name}");
        }

        private static string CreateMaterialAt(string folder, string name)
        {
            var material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            string path = $"{folder}/{name}.mat";
            AssetDatabase.CreateAsset(material, path);
            return path;
        }
    }
}
