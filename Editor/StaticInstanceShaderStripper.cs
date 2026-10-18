using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace StaticInstances.Editor
{
    // Build-time DOTS_INSTANCING_ON variant stripping. Specification:
    //  - Runs only when EVERY render pipeline asset the build references
    //    (Graphics default + all quality levels, same enumeration URP's own
    //    build processors use) has the GPU Resident Drawer disabled — a GRD
    //    build needs the DOTS variants of everything it draws, and enabling
    //    GRD at runtime on a stripped build is out of scope (mirrors Unity's
    //    shader prefiltering design).
    //  - Keeps the variants of shaders detected under Static Instances
    //    SIS content through IStaticInstanceBakeTarget:
    //    the build's own scenes, Static Instances prefabs anywhere in the
    //    project, and whatever StaticInstanceShaderStripperHooks subscribers
    //    contribute. Runtime-constructed owners are out of scope; the manual
    //    keep-list on the settings covers those.
    //  - Bundled scene delivery: the Keep All guard runs, but the STRIPPING
    //    does not arm itself.
    //      * The per-scene collector below reaches bundled scenes. The
    //        callback tests `isBuildingPlayer` rather than `report == null`,
    //        because SBP passes a null BuildReport during a real Addressables
    //        content build. Legacy BuildPipeline.BuildAssetBundles always
    //        passes a real report.
    //      * Stripping itself never ACTIVATES on a bundle path from here,
    //        because IPreprocessBuildWithReport — which evaluates the gate,
    //        seeds the manual keep list and sets Active — does not fire for
    //        either bundled path. Bundled content is therefore un-stripped
    //        rather than broken, and un-arming is the resting state on
    //        purpose: nothing may strip using a keep-set it did not build.
    //        Arming a bundle build is the opt-in Addressables build script's
    //        job, which collects before the build starts.
    //  - BatchRendererGroup Variants must stay Keep All (user
    //    responsibility); a build with Static Instances content and any other
    //    mode fails fast instead of silently rendering nothing.
    //
    // Collection and the Keep All guard use the shared bake-target
    // enumeration, so Scatter-only scenes declare all shaders they draw.
    internal static class StaticInstanceShaderStripping
    {
        internal static readonly HashSet<Shader> KeptShaders = new HashSet<Shader>();
        internal static readonly HashSet<Shader> StrippedShaders = new HashSet<Shader>();
        internal static readonly Dictionary<Shader, int> StripCounts =
            new Dictionary<Shader, int>();
        internal static bool Active;

        internal static void Reset()
        {
            KeptShaders.Clear();
            StrippedShaders.Clear();
            StripCounts.Clear();
            Active = false;
        }

        internal static void AddMaterials(List<Material> materials, bool canFailBuild)
        {
            foreach (Material material in materials)
            {
                if (material == null || material.shader == null)
                    continue;
                AddShader(material.shader, canFailBuild);
            }
        }

        // canFailBuild for the same reason RequireKeepAll takes it: the
        // belt below throws, and a throw raised while a bundled build is
        // running is swallowed by SBP — so on that path the ordering failure
        // is reported as an error instead of pretending to stop a build it
        // cannot stop. A build script collecting BEFORE the build can still
        // throw: its exception becomes a failed build result.
        internal static void AddShader(Shader shader, bool canFailBuild)
        {
            if (!KeptShaders.Add(shader))
                return;

            // The scene processors feed this list before the build compiles
            // the scene's shaders; if a shader was already stripped by the
            // time it turns out to be SIS-used, that ordering assumption
            // broke — fail the build instead of shipping a broken renderer.
            if (StrippedShaders.Contains(shader))
            {
                StaticInstanceBuildProcessor.Refuse(
                    canFailBuild,
                    $"Static Instances: shader '{shader.name}' was " +
                    "collected as SIS-used after its DOTS_INSTANCING_ON " +
                    "variants were already stripped. Add it to the manual " +
                    "keep list in Project Settings > Graphics > " +
                    "Static Instances and rebuild.",
                    "that shader is shipping without the variants this content needs");
            }
        }

        // The condition without the reaction, so a caller that can neither
        // throw nor log — a build script deciding before the build whether to
        // refuse it — asks the same question the callback asks.
        // Returns null when the setting is correct.
        internal static string DescribeKeepAllViolation(Object context)
        {
            if (EditorGraphicsSettings.batchRendererGroupShaderStrippingMode ==
                BatchRendererGroupStrippingMode.KeepAll)
            {
                return null;
            }

            // "Static Instances content", not "Groups": Scatters and
            // hook-supplied content are both under this guard.
            return
                "Static Instances: this build contains Static " +
                "Instances content but Project Settings > Graphics > Shader " +
                "Stripping > BatchRendererGroup Variants is not 'Keep All'. " +
                "The BRG content would not render; set it to 'Keep All' and " +
                "rebuild." + (context != null ? $" (first found on '{context.name}')" : "");
        }

        // canFailBuild says whether a throw raised here would actually stop the
        // build. It would not on a bundled path: SBP swallows a
        // BuildFailedException thrown from IProcessSceneWithReport and reports
        // the content build as successful, so the
        // throw would only produce an exception log that reads like a crash.
        // Such a path reports an error instead, and the opt-in Addressables
        // build script is what refuses the build — before it starts, where a
        // refusal works.
        internal static void RequireKeepAll(Object context, bool canFailBuild)
        {
            string message = DescribeKeepAllViolation(context);
            if (message == null)
                return;

            if (canFailBuild)
                throw new BuildFailedException(message);

            Debug.LogError(
                message +
                " This is a bundled content build, which cannot be failed from a scene " +
                "processor — it is completing with content that will not render. Set the " +
                "Static Instances build script on your Addressables settings to have such a " +
                "build refused instead.");
        }
    }

    // Gate evaluation and manual keep-list seeding at build start, summary
    // log at build end.
    internal sealed class StaticInstanceStrippingBuildProcessor :
        IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        public int callbackOrder => -100;

        public void OnPreprocessBuild(BuildReport report)
        {
            StaticInstanceShaderStripping.Reset();

            var settings =
                GraphicsSettings.GetRenderPipelineSettings<StaticInstanceStrippingSettings>();
            if (settings == null || !settings.stripUnusedDotsInstancingVariants)
            {
                Debug.Log(
                    "Static Instances: DOTS variant stripping disabled " +
                    "(settings off or URP inactive).");
                return;
            }

            if (AnyPipelineAssetUsesGpuResidentDrawer(out string grdAssetName))
            {
                Debug.Log(
                    "Static Instances: DOTS variant stripping skipped — " +
                    $"'{grdAssetName}' enables the GPU Resident Drawer, which " +
                    "needs the DOTS variants of every shader it draws.");
                return;
            }

            StaticInstanceShaderStripping.Active = true;
            foreach (Shader shader in settings.additionalDotsInstancingShaders)
            {
                if (shader != null)
                    StaticInstanceShaderStripping.AddShader(shader, canFailBuild: true);
            }

            // The prefab walk lives in StaticInstanceBuildScan (callbackOrder
            // -99, straight after this): it has to run in every build for the
            // baked-prefab report.
            Debug.Log(
                "Static Instances: DOTS variant stripping active " +
                $"({StaticInstanceShaderStripping.KeptShaders.Count} shaders kept " +
                "from the manual list; the pre-build scan adds prefabs and build " +
                "scenes next).");
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            if (!StaticInstanceShaderStripping.Active)
                return;

            var log = new StringBuilder("Static Instances DOTS variant stripping:\n");
            log.AppendLine(
                $"  kept shaders: {StaticInstanceShaderStripping.KeptShaders.Count}, " +
                $"stripped shaders: {StaticInstanceShaderStripping.StrippedShaders.Count}");
            foreach (KeyValuePair<Shader, int> pair in StaticInstanceShaderStripping.StripCounts)
                log.AppendLine($"  stripped {pair.Value} DOTS variants: {pair.Key.name}");
            Debug.Log(log.ToString());
            StaticInstanceShaderStripping.Reset();
        }

        // Same render pipeline asset enumeration as URP's own build
        // processors: the Graphics default plus every quality level. Internal
        // because a bundled build evaluates the same gate from outside this
        // processor, which never runs for one.
        internal static bool AnyPipelineAssetUsesGpuResidentDrawer(out string assetName)
        {
            if (GraphicsSettings.defaultRenderPipeline is UniversalRenderPipelineAsset defaultAsset &&
                defaultAsset.gpuResidentDrawerMode != GPUResidentDrawerMode.Disabled)
            {
                assetName = defaultAsset.name;
                return true;
            }

            for (int i = 0; i < QualitySettings.count; i++)
            {
                if (QualitySettings.GetRenderPipelineAssetAt(i) is UniversalRenderPipelineAsset asset &&
                    asset.gpuResidentDrawerMode != GPUResidentDrawerMode.Disabled)
                {
                    assetName = asset.name;
                    return true;
                }
            }

            assetName = null;
            return false;
        }

        // The prefab walk lives in StaticInstanceBuildScan.ScanPrefabs: this
        // processor returns early when stripping does not engage, and the
        // baked-prefab report has to run in every build.
    }

    // Extension point for content whose materials are not reachable from a
    // Static Instances component in the scene — e.g. a spawner that
    // instantiates its content at runtime, so its serialized material sets
    // are the only build-time evidence of what it renders with. Subscribers
    // append materials for one scene root and register from an
    // [InitializeOnLoadMethod]. Keeping this a hook is what lets
    // Runtime + Editor be copied into another project on their own.
    //
    // Subscribers are invoked on EVERY build, not only when variant stripping
    // engages: a hook that returns materials puts its scene under the Keep All
    // guard, and that guard has to hold whether or not this build strips.
    public static class StaticInstanceShaderStripperHooks
    {
        // System.Action is spelled out: this file deliberately avoids
        // "using System" because UnityEngine.Object and System.Object would
        // then be an ambiguous reference throughout.
        public static event System.Action<GameObject, List<Material>> CollectAdditionalMaterials;

        internal static void Collect(GameObject root, List<Material> materials)
        {
            CollectAdditionalMaterials?.Invoke(root, materials);
        }
    }

    // Per-scene collection (player and AssetBundle builds both call this)
    // plus the Keep All fail-fast for any scene that contains Static
    // Instances content.
    internal sealed class StaticInstanceSceneShaderCollector : IProcessSceneWithReport
    {
        public int callbackOrder => 0;

        public void OnProcessScene(UnityEngine.SceneManagement.Scene scene, BuildReport report)
        {
            // Editor play mode also invokes this callback; only builds matter —
            // and `report == null` is not that test. SBP calls this
            // during a real Addressables content build with a null report.
            // See the bake processor for the discriminator.
            if (report == null && !BuildPipeline.isBuildingPlayer)
                return;

            CollectScene(scene, canFailBuild: report != null);
        }

        // The body, split out of the callback so the Editor tests
        // can drive it: a BuildReport cannot be constructed. The callback is
        // the only shipping caller. canFailBuild carries the one thing the
        // report was really being read for: whether this path can stop the
        // build.
        internal static void CollectScene(UnityEngine.SceneManagement.Scene scene, bool canFailBuild)
        {
            bool anyOwner = false;
            var materials = new List<Material>();
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                // The guard and the collection are one loop because they read the SAME
                // enumeration, so they cannot drift apart. The
                // interface is the enumeration — a future third component
                // kind joins both at once.
                foreach (IStaticInstanceBakeTarget target in
                    root.GetComponentsInChildren<IStaticInstanceBakeTarget>(true))
                {
                    if (!anyOwner)
                    {
                        anyOwner = true;
                        StaticInstanceShaderStripping.RequireKeepAll((Object)target, canFailBuild);
                    }

                    materials.Clear();
                    target.CollectReferencedMaterials(materials);
                    if (StaticInstanceShaderStripping.Active)
                        StaticInstanceShaderStripping.AddMaterials(materials, canFailBuild);
                }

                // Deliberately OUTSIDE the Active gate: hook-supplied content
                // must reach RequireKeepAll as well as AddMaterials, so a scene
                // whose only Static Instances content arrives by hook is still
                // checked for BatchRendererGroup Variants. The guard must run
                // whether or not this build strips; only AddMaterials is
                // stripping's business.
                materials.Clear();
                StaticInstanceShaderStripperHooks.Collect(root, materials);
                if (materials.Count == 0)
                    continue;

                if (!anyOwner)
                {
                    anyOwner = true;
                    StaticInstanceShaderStripping.RequireKeepAll(root, canFailBuild);
                }

                if (StaticInstanceShaderStripping.Active)
                    StaticInstanceShaderStripping.AddMaterials(materials, canFailBuild);
            }
        }
    }

    // The stripping itself: DOTS_INSTANCING_ON variants of every shader
    // outside the keep list are removed. Disabled variants are never touched.
    // Ordered after URP's own preprocessors.
    internal sealed class StaticInstanceDotsVariantStripper : IPreprocessShaders
    {
        public int callbackOrder => 1000;

        public void OnProcessShader(
            Shader shader,
            UnityEditor.Rendering.ShaderSnippetData snippet,
            IList<UnityEditor.Rendering.ShaderCompilerData> data)
        {
            if (!StaticInstanceShaderStripping.Active)
                return;
            if (StaticInstanceShaderStripping.KeptShaders.Contains(shader))
                return;

            var dotsKeyword = new ShaderKeyword(shader, "DOTS_INSTANCING_ON");
            int stripped = 0;
            for (int i = data.Count - 1; i >= 0; i--)
            {
                if (!data[i].shaderKeywordSet.IsEnabled(dotsKeyword))
                    continue;
                data.RemoveAt(i);
                stripped++;
            }

            if (stripped == 0)
                return;

            StaticInstanceShaderStripping.StrippedShaders.Add(shader);
            StaticInstanceShaderStripping.StripCounts.TryGetValue(shader, out int total);
            StaticInstanceShaderStripping.StripCounts[shader] = total + stripped;
        }
    }

    // The settings container is build-time only; remove it from players so
    // the manual keep list never drags shader references into a build.
    internal sealed class StaticInstanceStrippingSettingsStripper :
        IRenderPipelineGraphicsSettingsStripper<StaticInstanceStrippingSettings>
    {
        public bool active => true;

        public bool CanRemoveSettings(StaticInstanceStrippingSettings settings) => true;
    }

    // Editor workflow settings mean nothing at runtime; keep them out of
    // players for the same reason.
    internal sealed class StaticInstanceEditorSettingsStripper :
        IRenderPipelineGraphicsSettingsStripper<StaticInstanceEditorSettings>
    {
        public bool active => true;

        public bool CanRemoveSettings(StaticInstanceEditorSettings settings) => true;
    }
}
