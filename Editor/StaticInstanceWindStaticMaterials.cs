using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace StaticInstances.Editor
{
    // Generation of the wind-stripped material variants that SIS draws for
    // SpeedTree content. SIS does not simulate wind, so every draw group whose
    // material enables a wind keyword draws its static clone instead.
    //
    // WHAT THE VARIANT IS. Not shader authoring, and not a separate shader:
    // the "optimized static vertex shader" already ships inside the SpeedTree9
    // graph as `shader_feature` keywords, vertex stage only. With every wind
    // keyword off, the compiled vertex shader contains no wind evaluation at all
    // and the fragment stage is untouched — so the still tree looks identical
    // while the wind ALU disappears.
    //
    // WHICH KEYWORDS. However many the material actually declares — NOT a
    // hardcoded five. A project-side graph may declare and enable exactly one
    // (`_WIND_SHARED`), so a blanket disable of five would be
    // writing keywords the shader never heard of. Constructing a `LocalKeyword` for
    // an undeclared name also logs an error, so the lookup goes through the shader's
    // own keyword space, which answers quietly.
    //
    // WHY A SERIALIZED ASSET. The keywords are `shader_feature`: the stripped
    // variant is compiled into the build only if something serialized asks for it.
    // A material cloned at runtime would find its variant missing in a player.
    //
    // WHY THIS IS NOT PART OF THE AUTOMATIC BAKE. It writes assets, and the
    // build-time bake runs inside scene processing where creating assets is not
    // something to do. So generation is an editor-side act attached to the
    // explicit operations (the "Bake Sources" press and the scene batch), and a
    // group whose clone is missing falls back to its wind material, which reads
    // a zeroed wind record and stands still.
    public static class StaticInstanceWindStaticMaterials
    {
        // Project-level output: these are general generated artifacts, not
        // content belonging to one scene.
        //
        // `internal` rather than private so the clone GC reads it here rather than
        // repeating the literal: the generator owns the folder,
        // because the generator is what creates it.
        internal const string OutputFolder = "Assets/SIS_WindStatic";

        /// <summary>
        /// Generate (or refresh) the wind-stripped variant of every draw group in
        /// the open scenes whose material enables a wind keyword, and store it on
        /// the group.
        ///
        /// Every clone is rewritten from its source each time, so it cannot drift
        /// from it; see <see cref="EnsureClone"/>.
        /// </summary>
        /// <returns>How many draw groups now point at a clone.</returns>
        public static int GenerateForOpenScenes()
            => Generate(default, false);

        internal static int GenerateForScene(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded || EditorSceneManager.IsPreviewScene(scene))
                return 0;
            return Generate(scene, true);
        }

        private static int Generate(Scene targetScene, bool sceneOnly)
        {
            var byMaterial = new Dictionary<Material, Material>();
            int assigned = 0;
            int owners = 0;

            // The clone GC: which clones each SCENE ends up pointing at, collected
            // per scene rather than per owner because the ledger's row is the scene.
            // Recorded for every scene walked — including scenes that turn out to need no
            // clone, whose row then correctly becomes empty and stops keeping anything
            // alive. See StaticInstanceWindCloneRegistry.
            //
            // **NO material→clone TABLE is kept.**
            // Attachment stays the deterministic identity function in
            // `ClonePathFor`. The load-bearing reason: resolution runs
            // in contexts that must not WRITE — the build-time bake, on a scene copy,
            // possibly CI — so on a missing row nothing could create one there, and the
            // function has to exist regardless. A table could then only be a cache over
            // derivable truth. The one thing a table would enable, deduplicating
            // content-identical clones, is the content pipeline's to fix at the source:
            // real content differs per species.
            //
            // Clone path → the SOURCE material's name, per scene. The name is carried
            // because it is not part of the clone's filename (it is not part of
            // the identity and would make the path unstable under a rename), so the ledger is
            // where a human reads which species a row's clone came from.
            var clonesByScene = new Dictionary<string, Dictionary<string, string>>();
            var sceneNames = new Dictionary<string, string>();

            // Seeded from the LOADED SCENES rather than from the owners found below:
            // keying off owners would mean a scene whose Scatter is unbaked,
            // cleared or absent never gets a `Record` call, so its previous row would survive
            // verbatim and go on claiming clones — the "kept alive forever" direction
            // the replace-not-merge rule exists to prevent. Every loaded scene with a path
            // gets a row, and a scene that needs no clone correctly gets an empty one.
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene loaded = SceneManager.GetSceneAt(i);
                if (sceneOnly && loaded != targetScene)
                    continue;
                if (!loaded.IsValid() || string.IsNullOrEmpty(loaded.path))
                    continue;
                string guid = AssetDatabase.AssetPathToGUID(loaded.path);
                if (string.IsNullOrEmpty(guid) || clonesByScene.ContainsKey(guid))
                    continue;
                clonesByScene.Add(guid, new Dictionary<string, string>());
                sceneNames[guid] = loaded.name;
            }

#if UNITY_6000_4_OR_NEWER
            // 6000.4 deprecated FindObjectsSortMode. The order is not load-bearing:
            // clone paths derive from the source material and the registry sorts its rows.
            StaticInstanceScatter[] scatters = Object.FindObjectsByType<StaticInstanceScatter>(
                FindObjectsInactive.Include);
#else
            StaticInstanceScatter[] scatters = Object.FindObjectsByType<StaticInstanceScatter>(
                FindObjectsInactive.Include, FindObjectsSortMode.InstanceID);
#endif
            foreach (StaticInstanceScatter scatter in scatters)
            {
                if (sceneOnly && scatter.gameObject.scene != targetScene)
                    continue;
                BakedDrawGroup[] groups = scatter.GetBakedDrawGroups();
                if (!scatter.HasBakedData || groups == null)
                {
                    continue;
                }

                // The row this owner's clones belong to. Seeded above from the loaded
                // scenes, so the lookup finds it; an owner in an unsaved scene has no
                // GUID and contributes to no row — there is nothing stable to key one on,
                // and the next generation after a save records it.
                Scene ownerScene = scatter.gameObject.scene;
                string sceneGuid = ownerScene.IsValid() && !string.IsNullOrEmpty(ownerScene.path)
                    ? AssetDatabase.AssetPathToGUID(ownerScene.path)
                    : null;
                if (!string.IsNullOrEmpty(sceneGuid) && !clonesByScene.ContainsKey(sceneGuid))
                {
                    clonesByScene.Add(sceneGuid, new Dictionary<string, string>());
                    sceneNames[sceneGuid] = ownerScene.name;
                }

                bool changed = false;
                for (int g = 0; g < groups.Length; g++)
                {
                    if (!StaticInstanceSpeedTreeWind.MaterialEnablesWind(groups[g].Material))
                        continue;

                    if (!byMaterial.TryGetValue(groups[g].Material, out Material clone))
                    {
                        clone = EnsureClone(groups[g].Material);
                        byMaterial.Add(groups[g].Material, clone);
                    }
                    if (clone == null)
                        continue;

                    assigned++;
                    if (!string.IsNullOrEmpty(sceneGuid))
                    {
                        string clonePath = AssetDatabase.GetAssetPath(clone);
                        if (!string.IsNullOrEmpty(clonePath))
                            clonesByScene[sceneGuid][clonePath] = groups[g].Material.name;
                    }

                    if (groups[g].StaticMaterial == clone)
                        continue;
                    groups[g].StaticMaterial = clone;
                    changed = true;
                }

                if (!changed)
                    continue;

                // The draw groups live in the serialized bake data, so a change
                // here is a scene change. Left dirty rather than saved, the same
                // call every other bake path makes.
                EditorUtility.SetDirty(scatter);
                EditorSceneManager.MarkSceneDirty(scatter.gameObject.scene);
                owners++;
            }

            AssetDatabase.SaveAssets();

            // After the save, so a row never claims a clone the save could still have
            // failed to write.
            foreach (KeyValuePair<string, Dictionary<string, string>> entry in clonesByScene)
            {
                StaticInstanceWindCloneRegistry.Record(
                    entry.Key, sceneNames[entry.Key], entry.Value);
            }

            if (assigned == 0)
            {
                Debug.Log(
                    "Static Instances: no wind-stripped materials to generate — no baked " +
                    "Scatter draw group in the open scenes uses a wind-enabled material.");
            }
            else
            {
                Debug.Log(
                    $"Static Instances: {byMaterial.Count} wind-stripped material variant(s) in " +
                    $"'{OutputFolder}', covering {assigned} draw group(s)" +
                    (owners > 0
                        ? $"; {owners} Scatter component(s) updated and their scene left dirty."
                        : "; nothing needed updating."));
            }

            return assigned;
        }

        /// <summary>
        /// The clone that already exists for a source material, or null. A pure
        /// lookup: it creates nothing, so it is safe on the build-time bake path
        /// where writing assets is not something to do.
        ///
        /// This is what carries `StaticMaterial` across a re-bake.
        /// `BakeFromHierarchy` replaces the whole draw-group array, so a scene that
        /// was stale at build time would otherwise ship with `StaticMaterial == null`
        /// on every group. Since <see cref="ClonePathFor"/> is deterministic from the
        /// source material, re-resolving is cheaper than storing the reference.
        /// </summary>
        internal static Material FindExistingClone(Material source)
        {
            string path = ClonePathFor(source);
            if (string.IsNullOrEmpty(path))
                return null;

            var clone = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (clone == null)
                return null;

            // A STALE clone must read as no clone. The re-attach runs in the
            // automatic bakes, which cannot regenerate anything — so without this
            // check, a source material edited after the last explicit press would be
            // re-attached to its outdated variant and shipped, drawing visibly
            // different content from its source. Null is the safe fallback.
            return RecordedSourceStamp(path) == CurrentSourceStamp(source) ? clone : null;
        }

        /// <summary>
        /// The bake-time re-attach installed as
        /// <see cref="StaticInstanceEditorBridge.AssignStaticMaterials"/>: every
        /// group whose material enables a wind keyword gets its existing, current
        /// clone (or null). Creates nothing.
        /// </summary>
        internal static void AssignExistingClones(BakedDrawGroup[] groups)
        {
            if (groups == null)
                return;
            for (int g = 0; g < groups.Length; g++)
            {
                groups[g].StaticMaterial =
                    StaticInstanceSpeedTreeWind.MaterialEnablesWind(groups[g].Material)
                        ? FindExistingClone(groups[g].Material)
                        : null;
            }
        }

        /// <summary>
        /// The source's dependency hash, as a string. `GetAssetDependencyHash`
        /// changes whenever the asset's file or importer settings change, which
        /// covers both a standalone `.mat` and a SpeedTree sub-asset material (whose
        /// path is the `.st9`).
        /// </summary>
        private static string CurrentSourceStamp(Material source)
        {
            string path = AssetDatabase.GetAssetPath(source);
            return string.IsNullOrEmpty(path)
                ? string.Empty
                : AssetDatabase.GetAssetDependencyHash(path).ToString();
        }

        /// <summary>
        /// The stamp recorded when the clone was last written, kept in the clone's
        /// own importer `userData` — a Material has no spare serialized field, and
        /// this rides in the `.meta` beside the asset it describes.
        /// </summary>
        private static string RecordedSourceStamp(string clonePath)
        {
            AssetImporter importer = AssetImporter.GetAtPath(clonePath);
            return importer != null ? importer.userData : null;
        }

        private static void RecordSourceStamp(string clonePath, Material source)
        {
            AssetImporter importer = AssetImporter.GetAtPath(clonePath);
            if (importer == null)
                return;
            importer.userData = CurrentSourceStamp(source);
            importer.SaveAndReimport();
        }

        /// <summary>
        /// The wind-stripped variant of one material, created or refreshed in
        /// place. Null when the material has no wind keywords to strip — that is
        /// not an error, it is content that is already the static case.
        /// </summary>
        internal static Material EnsureClone(Material source)
        {
            if (source == null || source.shader == null)
                return null;

            List<string> toDisable = EnabledWindKeywords(source);
            if (toDisable.Count == 0)
                return null;

            string path = ClonePathFor(source);
            if (path == null)
                return null;

            EnsureFolder();

            var clone = new Material(source) { name = System.IO.Path.GetFileNameWithoutExtension(path) };
            DisableWindKeywords(clone, toDisable);

            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing == null)
            {
                AssetDatabase.CreateAsset(clone, path);
                RecordSourceStamp(path, source);
                return clone;
            }

            // Refreshed UNCONDITIONALLY: a check comparing only the shader and the
            // stripped keywords would miss any other edit to the source material —
            // albedo, a texture, the alpha cutoff — and the clone would draw
            // visibly different content from its source, with nothing reporting
            // it. Copying every time cannot go stale, and this runs only on an
            // explicit press.
            //
            // The source's dependency hash is recorded alongside anyway, because the
            // automatic bakes re-attach clones they cannot regenerate and need a way
            // to tell a current one from an outdated one.
            //
            // Overwrite in place so the GUID survives — every draw group already
            // referencing this clone keeps pointing at it.
            EditorUtility.CopySerialized(clone, existing);
            Object.DestroyImmediate(clone);
            // Keywords do not reliably survive CopySerialized, and here the keyword set is the asset's whole
            // reason to exist — so re-apply it on the persisted instance.
            DisableWindKeywords(existing, toDisable);
            EditorUtility.SetDirty(existing);
            RecordSourceStamp(path, source);
            return existing;
        }

        /// <summary>
        /// The wind keywords this material both declares and enables — the exact
        /// set the static variant has to turn off.
        /// </summary>
        internal static List<string> EnabledWindKeywords(Material material)
        {
            var enabled = new List<string>();
            if (material == null || material.shader == null)
                return enabled;

            LocalKeywordSpace space = material.shader.keywordSpace;
            foreach (string keyword in StaticInstanceSpeedTreeWind.Keywords)
            {
                LocalKeyword local = space.FindKeyword(keyword);
                if (local.isValid && material.IsKeywordEnabled(local))
                    enabled.Add(keyword);
            }
            return enabled;
        }

        /// <summary>
        /// Deterministic asset path for a source material's static variant:
        /// <c>SISW_{guid}_{localFileId}.mat</c>.
        ///
        /// **The identity is the guid+localId pair and nothing else.** SpeedTree materials
        /// are embedded sub-assets, so several materials share one GUID and two `.st9`
        /// files in different folders can hold identically named ones — a name collision
        /// here would silently draw one species with another's material, which is the class
        /// of bug a generated file name must not be able to cause.
        ///
        /// **Properties of this naming:**
        /// <list type="bullet">
        /// <item>the source name is not part of the key, so the name is a
        /// FIXED length — prefix + 32-character guid + a decimal id — independent of how
        /// long an artist's material name is, which keeps the path within Windows' limits
        /// (260 for `MAX_PATH`, 255 for a single component);</item>
        /// <item>the path is **stable under a source-material rename**;</item>
        /// <item>the `SISW_` prefix names the owner: this string is also the material's
        /// runtime `m_Name`, which is what a Frame Debugger or profiler shows with no
        /// folder context. `SISW` greps to exactly one generator, and it matches the
        /// product's own vocabulary (`SIS_WindStatic`).</item>
        /// </list>
        ///
        /// `localId` is DECIMAL because Unity writes fileIDs in decimal in YAML and
        /// `.meta` files, so the name can be matched against a serialized reference by eye.
        /// </summary>
        internal static string ClonePathFor(Material source)
        {
            if (source == null)
                return null;
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(source, out string guid, out long localId))
                return null;
            return $"{OutputFolder}/SISW_{guid}_{localId}.mat";
        }

        /// <summary>
        /// Turn a wind category off — properly.
        ///
        /// **`DisableKeyword` alone is not enough, and fails silently.** These are
        /// ShaderGraph boolean keywords, which are backed by a material FLOAT
        /// PROPERTY of the same name; validation re-derives the keyword from the
        /// property, so a keyword cleared without clearing the property comes back
        /// at the next import: the freshly written clone would read `_WIND_SHARED`
        /// enabled again after the save. The
        /// result would be a clone that costs a material and changes nothing,
        /// with nothing anywhere reporting a problem.
        ///
        /// The property is written only when it exists, so a hand-written shader
        /// whose `shader_feature` has no backing property still works through the
        /// keyword alone.
        /// </summary>
        private static void DisableWindKeywords(Material material, List<string> keywords)
        {
            LocalKeywordSpace space = material.shader.keywordSpace;
            foreach (string keyword in keywords)
            {
                if (material.HasProperty(keyword))
                    material.SetFloat(keyword, 0f);
                LocalKeyword local = space.FindKeyword(keyword);
                if (local.isValid)
                    material.DisableKeyword(local);
            }
        }

        private static void EnsureFolder()
        {
            if (AssetDatabase.IsValidFolder(OutputFolder))
                return;
            AssetDatabase.CreateFolder("Assets", OutputFolder.Substring("Assets/".Length));
        }

    }
}
