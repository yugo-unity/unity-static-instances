using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StaticInstances.Editor
{
    // One pre-build walk over the project's Static Instances content, serving
    // two jobs from a single pass.
    //
    //  1. Layer 2 — REPORT prefabs carrying baked data. Baked matrices are
    //     world-space, so a prefab can never use them; the inspector band is
    //     layer 1 and only fires when somebody looks, while a prefab can be
    //     created through five routes. A check on serialized state does not
    //     care how the state arrived, so this layer is what makes "a report
    //     nobody reads" impossible.
    //
    //  2. DECLARE the materials of scene content BEFORE any shader
    //     compiles. Collection is otherwise per scene, during scene
    //     processing, so a shader that build scene N declares is already
    //     stripped if scene M < N compiled it first. For example, when build
    //     scene 0 carries no Static Instances content, the shaders of a later
    //     scene would otherwise have to be hand-listed in the manual keep
    //     list to survive scene 0's pass.
    //
    // WHY THIS IS NOT PART OF THE STRIPPER'S OWN PASS. The stripper's prefab
    // walk runs only when stripping engages — settings on AND the GPU
    // Resident Drawer disabled in every render pipeline asset — so it does not
    // run for a project that keeps the drawer. Job 1 has to run in every
    // build, so it lives here; job 2 is stripping's business and checks for it.
    //
    // ORDER. callbackOrder is -99, one after the stripper's -100: the
    // stripper decides whether stripping is active and seeds the keep list
    // from prefabs, and job 2 below is meaningless before that has happened.
    internal sealed class StaticInstanceBuildScan : IPreprocessBuildWithReport
    {
        public int callbackOrder => -99;

        public void OnPreprocessBuild(BuildReport report)
        {
            var bakedPrefabs = new List<string>();
            ScanPrefabs(bakedPrefabs);
            ReportBakedPrefabs(bakedPrefabs);

            // Job 2 only matters when something is going to be stripped.
            if (StaticInstanceShaderStripping.Active)
                PreScanScenes(EnabledBuildSettingsScenePaths(), null);
        }

        // The player build's scene enumeration. A bundled build has a
        // different one — its own delivery list — which is why the pass below
        // takes paths instead of reading this itself.
        internal static IEnumerable<string> EnabledBuildSettingsScenePaths()
        {
            foreach (EditorBuildSettingsScene entry in EditorBuildSettings.scenes)
            {
                if (entry.enabled && !string.IsNullOrEmpty(entry.path))
                    yield return entry.path;
            }
        }

        // Every prefab that carries Static Instances content, using the same
        // OR-ed script-path pre-filter the stripper uses so a Scatter-only
        // prefab is not skipped unopened.
        //
        // Inherits that pre-filter's one known gap, deliberately:
        // GetDependencies is non-recursive, so a prefab whose only Static
        // Instances component sits on a NESTED prefab is skipped. The nested
        // prefab is walked as its own t:Prefab, so the exposure is limited to
        // baked-data overrides an outer prefab applies to a nested one.
        internal static void ScanPrefabs(List<string> bakedPrefabs)
        {
            string scatterPath = FindScriptPath(typeof(StaticInstanceScatter));
            var materials = new List<Material>();

            foreach (string guid in AssetDatabase.FindAssets("t:Prefab"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!MayContainStaticInstances(path, scatterPath))
                    continue;

                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null)
                    continue;

                foreach (IStaticInstanceBakeTarget target in
                    prefab.GetComponentsInChildren<IStaticInstanceBakeTarget>(true))
                {
                    if (target.HasBakedData)
                    {
                        bakedPrefabs.Add(
                            $"{path} ({((MonoBehaviour)target).name}, " +
                            $"{target.BakedInstanceCount:N0} instances)");
                    }

                    if (!StaticInstanceShaderStripping.Active)
                        continue;

                    materials.Clear();
                    target.CollectReferencedMaterials(materials);
                    StaticInstanceShaderStripping.AddMaterials(materials, canFailBuild: true);
                }
            }
        }

        private static void ReportBakedPrefabs(List<string> bakedPrefabs)
        {
            if (bakedPrefabs.Count == 0)
                return;

            // A warning, not a build failure. The data is inert rather than
            // wrong — the owner-matrix guard rejects it at Initialize and the
            // prefab draws correctly from runtime collection — so what is at
            // stake is asset size, which is not worth refusing a build over.
            var log = new StringBuilder(
                $"Static Instances: {bakedPrefabs.Count:N0} prefab(s) carry baked instance " +
                "data, which a prefab can never use — the matrices are world-space, valid " +
                "only at the transform they were baked at, and an instantiated prefab falls " +
                "back to runtime collection. The data is dead weight in the build. Select " +
                "each one and use 'Clear baked data'; bake the scene instance instead.\n");
            foreach (string entry in bakedPrefabs)
                log.AppendLine($"  {entry}");
            Debug.LogWarning(log.ToString());
        }

        // Open every scene of the delivery being built and declare its
        // Static Instances materials now, so nothing it draws with can be
        // stripped by an earlier scene's compilation.
        //
        // Only scenes this pass OPENED are closed again. Closing one that was
        // already open is rude and, when it is the only scene loaded, refused
        // by Unity ("Unloading the last loaded scene ... is not supported").
        //
        // It has two callers: the player build and the bundled-content build
        // script. It takes the
        // scene paths instead of reading Build Settings, because a bundled
        // build's delivery list is its own; and when `violations` is non-null it
        // also VALIDATES, since a bundled build cannot be failed from inside
        // (SBP swallows the throw), so its only working fail-fast is
        // a check made here, before the build starts. One traversal, two
        // consumers — the same reason the prefab walk lives beside it.
        internal static void PreScanScenes(IEnumerable<string> scenePaths, List<string> violations)
        {
            var alreadyOpen = new HashSet<string>();
            for (int i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                string path = EditorSceneManager.GetSceneAt(i).path;
                if (!string.IsNullOrEmpty(path))
                    alreadyOpen.Add(path);
            }

            var materials = new List<Material>();
            int scenesScanned = 0;
            int before = StaticInstanceShaderStripping.KeptShaders.Count;

            foreach (string scenePath in scenePaths)
            {
                if (string.IsNullOrEmpty(scenePath))
                    continue;

                bool weOpenedIt = !alreadyOpen.Contains(scenePath);
                Scene scene = EditorSceneManager.OpenScene(
                    scenePath, OpenSceneMode.Additive);
                scenesScanned++;
                try
                {
                    // Two concerns, one definition each, deliberately not
                    // fused into a single loop: declaration is stripping's
                    // business and happens only when this build strips, while
                    // validation runs whenever a caller asked for it and has
                    // to be drivable from a test.
                    if (StaticInstanceShaderStripping.Active)
                        DeclareSceneMaterials(scene, materials);

                    if (violations != null)
                        CollectSceneViolations(scene, violations);
                }
                finally
                {
                    if (weOpenedIt)
                        EditorSceneManager.CloseScene(scene, true);
                }
            }

            if (StaticInstanceShaderStripping.Active)
            {
                Debug.Log(
                    $"Static Instances: pre-declared {StaticInstanceShaderStripping.KeptShaders.Count - before} " +
                    $"additional shader(s) from {scenesScanned:N0} delivered scene(s), before any " +
                    "shader compiles. The per-scene collector still runs as the backstop.");
            }
        }

        // The declaration walk over one already-open scene: every Static
        // Instances material, plus whatever the hooks contribute, declared
        // before any shader compiles.
        private static void DeclareSceneMaterials(Scene scene, List<Material> materials)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (IStaticInstanceBakeTarget target in
                    root.GetComponentsInChildren<IStaticInstanceBakeTarget>(true))
                {
                    materials.Clear();
                    target.CollectReferencedMaterials(materials);
                    StaticInstanceShaderStripping.AddMaterials(materials, canFailBuild: true);
                }

                // Hook-supplied content declares here too, for the same
                // reason: its shaders are otherwise declared during scene
                // processing, which is too late.
                materials.Clear();
                StaticInstanceShaderStripperHooks.Collect(root, materials);
                if (materials.Count > 0)
                    StaticInstanceShaderStripping.AddMaterials(materials, canFailBuild: true);
            }
        }

        // One already-open scene's worth of the same walk, split out for the
        // reason `CollectScene` was split out of its callback: a test can
        // drive a scene it built, and a scene it built has to be a PREVIEW
        // scene here (an additive one throws the moment any case leaves an
        // untitled scene behind, and a preview scene keeps the user's open
        // scenes clean). The traversal below is the shipping caller.
        internal static void CollectSceneViolations(Scene scene, List<string> violations)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (IStaticInstanceBakeTarget target in
                    root.GetComponentsInChildren<IStaticInstanceBakeTarget>(true))
                {
                    CollectTargetViolations(scene, target, violations);
                }

                var hookMaterials = new List<Material>();
                StaticInstanceShaderStripperHooks.Collect(root, hookMaterials);
                if (hookMaterials.Count > 0)
                    AddKeepAllViolation(root, violations);
            }
        }

        // Conditions a bundled build has to be refused for, gathered
        // rather than thrown: the caller is a build script that reports them
        // through the build's own failure channel before the build starts.
        // These mirror checks the scene processors make; none can stop a
        // bundled build from inside one.
        internal static void CollectTargetViolations(
            Scene scene, IStaticInstanceBakeTarget target, List<string> violations)
        {
            AddKeepAllViolation((Object)target, violations);

            var behaviour = (MonoBehaviour)target;
            string nestingReason =
                StaticInstanceBakeUtility.UnsupportedNestingReason(behaviour);
            if (nestingReason != null)
                violations.Add($"{scene.name}/{behaviour.name}: {nestingReason}");

            bool hasRenderers =
                StaticInstanceOwnership.OwnedComponents<MeshRenderer>(behaviour).Length > 0;
            // SBP can swallow a scene-processor refusal; reject corrupt stored
            // matrices here when auto-bake cannot replace them from source content.
            bool invalidMatrices = ((StaticInstanceScatter)behaviour).HasStoredBakedMatrices &&
                !((StaticInstanceScatter)behaviour).ValidateBakedMatrices();
            if (invalidMatrices && (!hasRenderers ||
                !StaticInstanceBuildProcessor.WillAutoBake(behaviour, out _)))
            {
                violations.Add($"{scene.name}/{behaviour.name}: invalid matrix bake. " +
                    "Restore source content and re-bake this owner before building.");
            }
            if (!target.HasBakedData || !hasRenderers)
                return;

            if (!StaticInstanceBuildProcessor.WillAutoBake(behaviour, out string suppressionReason))
            {
                if (StaticInstanceBakeUtility.IsBakeStale(behaviour))
                {
                    violations.Add(
                        $"{scene.name}/{behaviour.name}: baked data is stale and {suppressionReason}, " +
                        "so nothing would correct it — and staleness cannot be detected outside the " +
                        "editor, so the player would draw the stale bake as if it were current.");
                }
            }
        }

        private static void AddKeepAllViolation(Object context, List<string> violations)
        {
            string message = StaticInstanceShaderStripping.DescribeKeepAllViolation(context);
            if (message != null && !violations.Contains(message))
                violations.Add(message);
        }

        // Duplicated from the stripper rather than shared: that copy is
        // private to a build processor that may not run at all, and this pass
        // must not depend on it having run. Both are three lines.
        private static bool MayContainStaticInstances(
            string prefabPath, string scatterScriptPath)
        {
            if (scatterScriptPath == null)
                return true;

            string[] dependencies = AssetDatabase.GetDependencies(prefabPath, false);
            return System.Array.IndexOf(dependencies, scatterScriptPath) >= 0;
        }

        private static string FindScriptPath(System.Type type)
        {
            foreach (string guid in AssetDatabase.FindAssets($"{type.Name} t:MonoScript"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                if (script != null && script.GetClass() == type)
                    return path;
            }

            return null;
        }
    }
}
