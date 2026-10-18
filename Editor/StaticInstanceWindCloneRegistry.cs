using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StaticInstances.Editor
{
    // Collection of the wind-stripped material clones in
    // `Assets/SIS_WindStatic/`.
    //
    // THE PROBLEM. A source material edited or retired leaves its clone behind with
    // nothing collecting it, and every naive fix is unsafe.
    //
    // WHY A REGISTRY IS NEEDED AT ALL. Clone paths are deterministic from the source
    // (`{guid}_{localFileId}`), so two scenes using the same species point at the SAME
    // clone: the relation is many-to-many. Deleting "this scene's clones" would break
    // another scene's serialized `StaticMaterial` reference into the silent null
    // fallback — a still tree with nothing reporting why. And the question "is anything
    // still using this clone" cannot be answered from the OPEN scenes, because the
    // references live in the bake data of CLOSED ones.
    //
    // WHAT THIS STORES, AND WHERE. One row per scene: its GUID, its name, and the clones
    // its bake data pointed at when the clones were last generated. The name sits beside
    // the GUID deliberately — rows for deleted scenes are accepted rather than chased, so a
    // human needs to be able to read the list and prune it.
    //
    // **The store is a ScriptableObject asset BESIDE THE CLONES**, not a
    // `UserSettings/` JSON file. The record describes COMMITTED content: a
    // fresh `git clone` has no `UserSettings/`, so every clone a repository
    // carries would be unknown on every new machine — and unknown means keep
    // for ever, which makes the collector dead for every new team member. The
    // ledger has to share its clones' fate, and inside `Assets/SIS_WindStatic/`
    // it does: whatever the project's version control does with the clones, it
    // does with the ledger. `StaticInstanceWindCloneLedger` carries the rest of
    // the rationale, including why a row may never hold a `Material`
    // reference. Losing the asset must still fail SAFE (see below).
    //
    // TWO INDEPENDENT SAFEGUARDS, because one of them is not enough:
    //
    //   1. **Only registry-KNOWN clones are ever deleted.** A clone with no row
    //      anywhere is invisible to the sweep and survives. That is also what
    //      makes a lost or unreadable ledger harmless: the GC forgets, and
    //      forgetting means keeping.
    //   2. **A live dependency check vetoes every candidate.** Rule 1 alone does NOT
    //      close the hole its own rationale describes: a clone known through scene A's
    //      row can still be referenced by a closed, committed scene B
    //      that has no row — so clearing A would make it "referenced by zero rows" and
    //      delete something B needs. Before deleting anything, this asks the asset
    //      database which scenes and prefabs actually depend on the clone. That is a fact
    //      about the project rather than a record of what we happened to write down, so
    //      it is the safeguard that holds when the registry is wrong or incomplete.
    //
    //      **The check reads the IMPORTED asset**: the scene being cleared
    //      still references every clone until it is saved, so the veto would
    //      block everything and blame the very scene the user just cleared. Two
    //      things handle it — the caller saves before collecting *and checks
    //      that the save succeeded*, and the cleared scene is excluded from the
    //      scan by name. The check on the save is not belt-and-braces: the
    //      exclusion is only safe while the disk copy is current, so a failed
    //      save must collect nothing.
    //
    //      **And the imported state is not the whole truth**, so the scan reads the loaded
    //      scenes' SCATTER BAKE DATA from memory first — the product's own clone
    //      references, not everything a scene could hold; `AddLiveBlockers` states the
    //      scope and the two references it does not see. A loaded scene with
    //      unsaved bake data references clones that nothing on disk mentions — reachable
    //      rather than exotic, because the bake re-attaches an existing clone without
    //      recording a row and the Clear is scoped to the active scene, so an additively
    //      loaded dirty scene can be the only referrer. Neither a row nor the on-disk
    //      check would see it.
    //
    // The registry still earns its place: it is what makes the *candidate* set small and
    // scoped to the Clear the user pressed, so the GC never proposes deleting a clone
    // it never generated.
    //
    // THREE STATES, not two. A clone is either CLAIMED (some scene's row lists
    // it), an ORPHAN (known, claimed by nobody, kept because something outside
    // the registry still references it or the delete was refused), or UNKNOWN
    // (no record at all — permanently protected by rule 1). Without the orphan
    // state, a clone the veto legitimately kept would lose its row and become
    // UNKNOWN, so nothing could ever collect it again even after the blocking
    // asset was retired. Every Clear reconsiders the orphans.
    internal static class StaticInstanceWindCloneRegistry
    {
        // Beside the clones, for the reason in the header. `SISW_` matches the clones' own
        // prefix so the folder greps to one owner, and the asset is not a
        // `Material`, so `UnknownClonesOnDisk`'s `t:Material` sweep cannot mistake the
        // ledger for something to report.
        private static string DefaultRegistryPath =>
            $"{StaticInstanceWindStaticMaterials.OutputFolder}/SISW_CloneLedger.asset";

        // Test seam, and it is a safety measure rather than a convenience: the ledger is a
        // REAL asset in the developer's own project, so a test that exercised the default
        // path would delete whatever this machine had recorded. Tests point this at a temp
        // asset and restore it; production never sets it.
        internal static string RegistryPathOverride;

        private static string RegistryPath =>
            string.IsNullOrEmpty(RegistryPathOverride) ? DefaultRegistryPath : RegistryPathOverride;

        // One source for the folder, taken from the generator that writes it:
        // duplicated, a moved output folder would leave `UnknownClonesOnDisk`
        // silently reporting nothing and the Clear report's unknown-clone line quietly
        // absent — a failure whose only symptom is a missing sentence.
        private static string CloneFolder => StaticInstanceWindStaticMaterials.OutputFolder;

        // The store is the ScriptableObject — the row and entry
        // shapes, and why they hold strings rather than object references, live on the type.
        private static string PathOf(StaticInstanceWindCloneLedger.CloneEntry entry) =>
            entry != null ? entry.Path : null;

        /// <summary>
        /// Replace one scene's row with the clones its bake data now points at.
        ///
        /// Replacing rather than merging is deliberate: a scene that stopped using a
        /// species should stop keeping its clone alive, and the generation pass always
        /// computes the complete set for the scene it walks.
        /// </summary>
        /// <param name="clonesBySourceName">
        /// Clone asset path → the source material's name, which the ledger carries because
        /// the clone's filename does not hold it.
        /// </param>
        internal static void Record(
            string sceneGuid, string sceneName, IEnumerable<KeyValuePair<string, string>> clonesBySourceName)
        {
            if (string.IsNullOrEmpty(sceneGuid))
                return;

            var clones = new List<StaticInstanceWindCloneLedger.CloneEntry>();
            if (clonesBySourceName != null)
            {
                foreach (KeyValuePair<string, string> clone in clonesBySourceName)
                {
                    if (string.IsNullOrEmpty(clone.Key) ||
                        clones.Exists(c => c.Path == clone.Key))
                    {
                        continue;
                    }
                    clones.Add(new StaticInstanceWindCloneLedger.CloneEntry
                    {
                        Path = clone.Key,
                        SourceName = clone.Value,
                    });
                }
            }

            // An empty row only matters to a ledger that already exists, where it releases
            // the clones the scene's previous row claimed. With no ledger there is nothing
            // to release, so creating one would leave `Assets/SIS_WindStatic/` behind in a
            // project that has no wind-receiving draw group (no SpeedTree) at all.
            StaticInstanceWindCloneLedger store = Load(createIfMissing: clones.Count > 0);
            if (store == null)
                return;

            StaticInstanceWindCloneLedger.SceneRow row =
                store.Rows.Find(r => r.SceneGuid == sceneGuid);
            if (row == null)
            {
                row = new StaticInstanceWindCloneLedger.SceneRow { SceneGuid = sceneGuid };
                store.Rows.Add(row);
            }

            row.SceneName = sceneName;
            row.Clones = clones;

            Save(store);
        }

        /// <summary>
        /// Drop <paramref name="sceneGuid"/>'s row and delete the clones it held that
        /// nothing else needs — the Clear-triggered collection.
        /// </summary>
        /// <returns>The clone paths actually deleted.</returns>
        /// <param name="ignoredReferrerPath">
        /// An asset whose references do NOT count as a reason to keep a clone — the scene
        /// being cleared. **This parameter is required**: the
        /// dependency check reads the IMPORTED asset, so the scene the user just cleared
        /// still references every clone until it is saved, and without the exclusion it
        /// would veto all of them, blame itself in the log, and (because the row has
        /// already been removed) leave them registry-unknown and therefore protected
        /// for good.
        ///
        /// ⚠ **This exclusion REQUIRES that the caller has verified the scene was saved.**
        /// Excluding an asset removes the only safeguard
        /// that can see it, so passing the path of a scene whose save FAILED asks this
        /// function to delete clones that scene still references. `ClearAllInScene` checks
        /// `SaveScene`'s return and collects nothing when it is false; a future second
        /// caller owes the same check. **No default value, deliberately**: a
        /// caller with nothing to exclude has to say `null` on purpose.
        /// </param>
        internal static List<string> CollectForClearedScene(
            string sceneGuid, string ignoredReferrerPath)
        {
            var deleted = new List<string>();
            if (string.IsNullOrEmpty(sceneGuid))
                return deleted;

            StaticInstanceWindCloneLedger store = Load();
            if (store == null)
                return deleted;

            // Candidates are this scene's clones PLUS the orphan set — clones a previous
            // Clear decided to keep. Orphans are the "known, claimed by no scene" state:
            // dropping the row for a clone the veto kept would make it unknown, and
            // unknown means keep forever, so the GC could never revisit it once the
            // blocking asset was retired.
            int index = store.Rows.FindIndex(r => r.SceneGuid == sceneGuid);
            var candidates = new List<string>();
            // Source names ride along so a clone that becomes an orphan keeps the readable
            // half its filename does not carry.
            var sourceNames = new Dictionary<string, string>();
            if (index >= 0)
            {
                foreach (StaticInstanceWindCloneLedger.CloneEntry entry in store.Rows[index].Clones)
                {
                    string path = PathOf(entry);
                    if (string.IsNullOrEmpty(path))
                        continue;
                    if (!candidates.Contains(path))
                        candidates.Add(path);
                    sourceNames[path] = entry.SourceName;
                }
                store.Rows.RemoveAt(index);
            }
            foreach (StaticInstanceWindCloneLedger.CloneEntry orphan in store.Orphans)
            {
                string path = PathOf(orphan);
                if (string.IsNullOrEmpty(path))
                    continue;
                if (!candidates.Contains(path))
                    candidates.Add(path);
                if (!sourceNames.ContainsKey(path))
                    sourceNames[path] = orphan.SourceName;
            }
            store.Orphans.Clear();

            // Whittle to the ones no REMAINING row claims. A clone another scene's bake
            // data points at is not a candidate at all, which is the many-to-many case
            // the registry exists for.
            var unclaimed = new List<string>();
            foreach (string path in candidates)
            {
                if (!string.IsNullOrEmpty(path) && !StillReferencedByARow(store, path))
                    unclaimed.Add(path);
            }

            // Safeguard 2, ONE scan for the whole press (a per-candidate scan would be
            // candidates x (scenes + prefabs) queries on the main thread).
            Dictionary<string, string> blockers = FindBlockingAssets(unclaimed, ignoredReferrerPath);

            foreach (string path in unclaimed)
            {
                if (blockers.TryGetValue(path, out string blocker))
                {
                    // Kept, and REMEMBERED as an orphan so a later Clear can retry it.
                    store.Orphans.Add(NewEntry(path, sourceNames));
                    Debug.Log(
                        $"Static Instances: keeping '{Path.GetFileName(path)}' — no scene's " +
                        $"bake data claims it, but '{blocker}' still references it. Recorded " +
                        "as an orphan, so the next Clear reconsiders it rather than losing " +
                        "track of it.");
                    continue;
                }

                if (AssetDatabase.DeleteAsset(path))
                {
                    deleted.Add(path);
                    continue;
                }

                // `DeleteAsset` returns false for an asset that is not there as well as
                // for one it refuses to remove. Treating
                // both as "refused, keep knowing about it" would accumulate dead paths in the
                // orphan set forever — and permanently defeat the candidate
                // early-out, since a path that can never be deleted can never be
                // accounted for either. A path with nothing behind it is simply forgotten.
                if (AssetDatabase.LoadAssetAtPath<Material>(path) != null)
                    store.Orphans.Add(NewEntry(path, sourceNames));   // really refused
            }

            Save(store);
            return deleted;
        }

        /// <summary>
        /// Which of <paramref name="candidates"/> some asset still references, and the
        /// first such asset's name — computed in a SINGLE pass over the project's scenes
        /// and prefabs.
        ///
        /// **Prefabs are scanned as well as scenes, and that is not defensive padding**:
        /// baked data can live inside a prefab as a development-time fallback, so a
        /// prefab's draw group can hold a
        /// `StaticMaterial` reference to a clone exactly as a scene's can. Scanning only
        /// scenes would delete it and leave the prefab's reference null, which degrades to
        /// a still tree with nothing said.
        ///
        /// One pass rather than one per candidate: the per-candidate shape rescans every
        /// scene and prefab for each candidate, on the main thread with no cancel, which
        /// at a dozen candidates is a freeze rather than a pause. Scoped to `Assets`
        /// deliberately — `FindAssets` with no folder spans
        /// `Packages/` too, and no package can reference a locally generated clone.
        /// </summary>
        private static Dictionary<string, string> FindBlockingAssets(
            List<string> candidates, string ignoredReferrerPath)
        {
            var blockers = new Dictionary<string, string>();
            if (candidates.Count == 0)
                return blockers;

            var wanted = new HashSet<string>(candidates);
            var searchFolders = new[] { "Assets" };

            // LOADED SCENES' SCATTER BAKE DATA FIRST, from memory rather than from the
            // importer (see `AddLiveBlockers`, which is the authority on what this does and does not
            // see). The dependency scan below answers about the asset ON DISK, so a
            // loaded scene with UNSAVED bake data is invisible to it — and that state is
            // reachable rather than exotic: the bake re-attaches an existing clone without
            // recording a row, and the Clear action is deliberately scoped to the active
            // scene, so an additively loaded, dirty, unrecorded scene B can be the only
            // thing referencing a clone when scene A is cleared. Both safeguards would
            // have missed it: no row to claim it, nothing on disk to veto it.
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene loaded = SceneManager.GetSceneAt(i);
                if (!loaded.IsValid() || !loaded.isLoaded || loaded.path == ignoredReferrerPath)
                    continue;

                foreach (GameObject root in loaded.GetRootGameObjects())
                {
                    AddLiveBlockers(
                        root.GetComponentsInChildren<StaticInstanceScatter>(true),
                        loaded.name, wanted, blockers);
                }
            }

            if (blockers.Count == wanted.Count)
                return blockers;

            foreach (string filter in new[] { "t:Scene", "t:Prefab" })
            {
                foreach (string guid in AssetDatabase.FindAssets(filter, searchFolders))
                {
                    string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                    if (string.IsNullOrEmpty(assetPath) || assetPath == ignoredReferrerPath)
                        continue;

                    foreach (string dependency in AssetDatabase.GetDependencies(assetPath, false))
                    {
                        if (wanted.Contains(dependency) && !blockers.ContainsKey(dependency))
                            blockers.Add(dependency, Path.GetFileNameWithoutExtension(assetPath));
                    }

                    if (blockers.Count == wanted.Count)
                        return blockers;   // every candidate already accounted for
                }
            }
            return blockers;
        }

        /// <summary>Is this clone path claimed by any remaining row?</summary>
        private static bool StillReferencedByARow(
            StaticInstanceWindCloneLedger store, string path)
        {
            for (int i = 0; i < store.Rows.Count; i++)
            {
                if (store.Rows[i].Clones.Exists(c => c.Path == path))
                    return true;
            }
            return false;
        }


        /// <summary>
        /// Every clone path the registry knows — claimed AND orphaned. Orphans belong here
        /// because "known" is what decides whether rule 1 protects a clone forever, and an
        /// orphan is precisely a clone we have decided to keep for now rather than forget.
        /// </summary>
        internal static List<string> KnownClones()
        {
            var known = new List<string>();
            StaticInstanceWindCloneLedger store = Load();
            if (store == null)
                return known;
            for (int i = 0; i < store.Rows.Count; i++)
            {
                foreach (StaticInstanceWindCloneLedger.CloneEntry entry in store.Rows[i].Clones)
                {
                    string path = PathOf(entry);
                    if (!string.IsNullOrEmpty(path) && !known.Contains(path))
                        known.Add(path);
                }
            }
            foreach (StaticInstanceWindCloneLedger.CloneEntry orphan in store.Orphans)
            {
                string path = PathOf(orphan);
                if (!string.IsNullOrEmpty(path) && !known.Contains(path))
                    known.Add(path);
            }
            return known;
        }

        /// <summary>
        /// Record which of <paramref name="wanted"/> these owners' LIVE bake data still
        /// points at. Separated from the scene walk so it can be tested: the walk itself
        /// needs scenes in `SceneManager`, and building those from a test would dirty the
        /// developer's open scene, and an in-memory edit the user did not make belongs to
        /// the user.
        ///
        /// **SCOPE, stated because the calling comments read broader than this**:
        /// what is read is `StaticInstanceScatter`'s baked draw groups'
        /// `StaticMaterial` — complete for every clone reference the product itself
        /// creates, since that is the only `Material` a Scatter serializes and wind is
        /// SIS-only, but NOT an in-memory equivalent of `GetDependencies`. Two references
        /// therefore remain invisible to both safeguards, both accepted as exotic and
        /// recorded rather than fixed: a clone **hand-assigned** to an ordinary
        /// `MeshRenderer` in a dirty loaded scene, and an open, dirty **Prefab stage**,
        /// whose preview scene is not in `SceneManager` at all.
        /// </summary>
        internal static void AddLiveBlockers(
            IEnumerable<StaticInstanceScatter> owners,
            string referrerName,
            HashSet<string> wanted,
            Dictionary<string, string> blockers)
        {
            if (owners == null)
                return;

            foreach (StaticInstanceScatter scatter in owners)
            {
                BakedDrawGroup[] groups = scatter != null ? scatter.GetBakedDrawGroups() : null;
                if (scatter == null || !scatter.HasBakedData || groups == null)
                {
                    continue;
                }

                for (int g = 0; g < groups.Length; g++)
                {
                    Material staticMaterial = groups[g].StaticMaterial;
                    if (staticMaterial == null)
                        continue;
                    string path = AssetDatabase.GetAssetPath(staticMaterial);
                    if (!string.IsNullOrEmpty(path) && wanted.Contains(path) &&
                        !blockers.ContainsKey(path))
                    {
                        blockers.Add(path, referrerName);
                    }
                }
            }
        }

        /// <summary>
        /// Clone assets on disk the registry does not know at all — neither claimed by a
        /// row nor held as an orphan. Reported, never
        /// swept: they are the clones rule 1 protects: never recorded, or forgotten by a lost ledger.
        /// </summary>
        internal static List<string> UnknownClonesOnDisk()
        {
            var unknown = new List<string>();
            if (!AssetDatabase.IsValidFolder(CloneFolder))
                return unknown;

            List<string> known = KnownClones();
            foreach (string guid in AssetDatabase.FindAssets("t:Material", new[] { CloneFolder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path) && !known.Contains(path))
                    unknown.Add(path);
            }
            return unknown;
        }

        /// <summary>
        /// The ledger asset, created on first write. **Returns null when it cannot be had**,
        /// and every caller treats null as "collect nothing".
        ///
        /// A missing or unreadable ledger must mean NO CANDIDATES rather than no
        /// constraints: the GC declining to collect costs disk space, while a GC acting on
        /// half a record breaks a scene. An asset that will not deserialize comes back as
        /// a null load, which lands in the same branch.
        /// </summary>
        private static StaticInstanceWindCloneLedger Load(bool createIfMissing = false)
        {
            var existing = AssetDatabase.LoadAssetAtPath<StaticInstanceWindCloneLedger>(RegistryPath);
            if (existing != null)
                return existing;

            // A file that exists but does not load as a ledger is corruption, and it is the
            // case that must not be papered over by writing a fresh one on top: replacing
            // it would forget rows that a scene may still depend on, which is the opposite
            // of failing safe. Report and decline.
            //
            // ⚠ THE EXISTENCE TEST IS `AssetPathExists`; the simpler forms are wrong:
            //
            //   * `AssetPathToGUID` returns a guid for a **recently deleted** path (its
            //     default is `IncludeRecentlyDeletedAssets`), not only for existing ones.
            //   * `LoadAssetAtPath<Object>() != null` reads false for a ledger whose
            //     `m_Script` line is broken — which is exactly the corruption this branch
            //     exists for — so the next `Record` would fall through to `CreateAsset`, and
            //     `CreateAsset` DELETES whatever occupies the path. The rows would go silently,
            //     with no warning, because the warning is in the branch not taken.
            if (AssetDatabase.AssetPathExists(RegistryPath))
            {
                Debug.LogWarning(
                    $"Static Instances: '{RegistryPath}' exists but is not a readable clone " +
                    "ledger. Treating it as unavailable, so this Clear collects nothing " +
                    "rather than guessing — move or delete it to start a fresh ledger.");
                return null;
            }

            if (!createIfMissing)
                return null;

            var created = ScriptableObject.CreateInstance<StaticInstanceWindCloneLedger>();
            EnsureFolderFor(RegistryPath);
            AssetDatabase.CreateAsset(created, RegistryPath);
            return created;
        }

        private static void EnsureFolderFor(string assetPath)
        {
            string folder = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(folder) || AssetDatabase.IsValidFolder(folder))
                return;
            int cut = folder.LastIndexOf('/');
            string parent = cut > 0 ? folder.Substring(0, cut) : "Assets";
            AssetDatabase.CreateFolder(parent, folder.Substring(cut + 1));
        }

        private static StaticInstanceWindCloneLedger.CloneEntry NewEntry(
            string path, Dictionary<string, string> sourceNames) =>
            new StaticInstanceWindCloneLedger.CloneEntry
            {
                Path = path,
                SourceName = sourceNames != null && sourceNames.TryGetValue(path, out string name)
                    ? name
                    : null,
            };

        /// <summary>
        /// Sort, then write THIS asset and nothing else.
        ///
        /// `SaveAssetIfDirty` rather than `AssetDatabase.SaveAssets()`: a project-wide
        /// flush from an editor operation sweeps in whatever else the Editor has dirtied
        /// (for example the URP global settings asset).
        ///
        /// The sort is for the committed case: rows in a stable order keep two people's
        /// presses from producing a merge conflict out of nothing but ordering.
        /// </summary>
        private static void Save(StaticInstanceWindCloneLedger store)
        {
            if (store == null)
                return;

            store.Sort();
            EditorUtility.SetDirty(store);
            AssetDatabase.SaveAssetIfDirty(store);

            // `SaveAssetIfDirty` returns void, so the only available signal is whether the
            // dirty flag cleared. Worth checking rather than assuming, since this file
            // is typically under version control: a read-only ledger would otherwise drop rows
            // silently, and the next Clear would treat clones as unknown — safe, but
            // permanently so, with nothing said.
            if (EditorUtility.IsDirty(store))
            {
                Debug.LogWarning(
                    $"Static Instances: the clone ledger '{RegistryPath}' would not save. " +
                    "Generated clones recorded in this pass will read as unknown — which " +
                    "keeps them rather than losing them — until it can be written; check " +
                    "whether the file is read-only.");
            }
        }
    }
}
