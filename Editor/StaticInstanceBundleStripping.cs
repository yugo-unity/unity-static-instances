using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using UnityEngine.Rendering;

namespace StaticInstances.Editor
{
    // DOTS variant stripping for a BUNDLED content build.
    //
    // Why this is an opt-in API instead of a callback. A bundled build has no
    // build start and no build end — neither IPreprocessBuildWithReport nor
    // IPostprocessBuildWithReport fires for an Addressables (SBP) or legacy
    // AssetBundle build — so nothing evaluates the gate, nothing
    // seeds the keep list, and nothing tears state down. Arming from inside
    // the per-scene callback instead is unsound twice over: a
    // content build whose SIS holds a material and NO scene compiles shaders
    // with zero scene callbacks (so the keep-set would be empty or, worse, a
    // previous build's), and the Editor loop does not tick between two builds
    // issued in one call, so no "clear on the next tick" teardown can separate
    // them. What is missing is a build identity, and the only thing that has
    // one is code that runs around the build: a build script.
    //
    // So the resting state is UNARMED ⇒ nothing is stripped, and a bundled
    // build strips only when a build script brackets it with the two calls
    // below. A project takes the reference build script in the Addressables
    // assembly beside this one, or calls these two methods from its own — two
    // lines — and marks that script with IStaticInstanceStrippingIntegrated so
    // the settings page can tell it is integrated.
    //
    // Nothing here references Addressables. That is deliberate: Runtime +
    // Editor must stay copyable into any project, so every Addressables type
    // lives in the version-gated assembly that calls in here.
    public static class StaticInstanceBundleStripping
    {
        // Set by the version-gated Addressables assembly when it loads, read by
        // the settings drawer. Null therefore means "the package is not
        // present", which the drawer must be able to establish WITHOUT touching
        // an Addressables type — hence a delegate rather than a type check.
        //
        // It answers "is there nothing to act on?" rather than "is a build
        // script integrated?", because two different states share the same
        // answer for the drawer's purposes: an integrated build script, and a
        // project with the package but no Addressables settings at all, which
        // runs no content builds and has nothing to configure.
        //
        // Internal, not public: a project's own build script calls
        // BeginContentBuild/EndContentBuild and implements
        // IStaticInstanceStrippingIntegrated — this is plumbing between our two
        // assemblies (see AssemblyInfo).
        private static System.Func<bool> nothingToActOnProbe;

        internal static void RegisterHintProbe(System.Func<bool> nothingToActOn)
        {
            nothingToActOnProbe = nothingToActOn;
        }

        // Readable and writable inside the assembly so a test can substitute a
        // probe and put the real one back: the states worth testing are the
        // three this delegate encodes (absent package, nothing to act on, hint
        // warranted).
        internal static System.Func<bool> HintProbe
        {
            get { return nothingToActOnProbe; }
            set { nothingToActOnProbe = value; }
        }

        // null = the package is absent (no question to answer);
        // true = the package is present and there is nothing to act on;
        // false = the hint is warranted.
        internal static bool? NothingToActOn
        {
            get { return nothingToActOnProbe == null ? (bool?)null : nothingToActOnProbe(); }
        }

        // Call BEFORE the bundled build runs, with the scene paths that build
        // is going to deliver.
        //
        // Returns null when the build may proceed. A non-null string is the
        // reason it must not: the conditions the scene processors fail a player
        // build for, which a bundled build cannot be failed for from inside
        // (SBP swallows the exception and reports success). The caller reports
        // it through the build's own failure channel — which is the whole point
        // of checking here instead.
        //
        // Arming is all-or-nothing and every call is a FULL reset: state never
        // accumulates across build operations, because a keep-set from another
        // operation describes other content.
        public static string BeginContentBuild(IEnumerable<string> scenePaths)
        {
            StaticInstanceShaderStripping.Reset();

            var settings =
                GraphicsSettings.GetRenderPipelineSettings<StaticInstanceStrippingSettings>();
            bool strippingRequested =
                settings != null && settings.stripUnusedDotsInstancingVariants;

            // The gate is the player build's gate, read here because nothing
            // else will: a GPU Resident Drawer build needs the DOTS variants of
            // everything it draws, so stripping must not engage for one.
            string grdAssetName = null;
            bool grdEnabled = strippingRequested &&
                StaticInstanceStrippingBuildProcessor.AnyPipelineAssetUsesGpuResidentDrawer(
                    out grdAssetName);

            var violations = new List<string>();

            if (strippingRequested && !grdEnabled)
            {
                StaticInstanceShaderStripping.Active = true;
                foreach (Shader shader in settings.additionalDotsInstancingShaders)
                {
                    if (shader != null)
                        StaticInstanceShaderStripping.AddShader(shader, canFailBuild: true);
                }

                // Both halves of the player build's pre-pass, and both are
                // required rather than nice to have: the prefab walk is the
                // only declaration of prefab-borne Static Instances content,
                // and the scene pre-scan is the only complete one for a
                // bundled build, whose per-scene callbacks are suppressed
                // entirely for a scene the content cache considers unchanged.
                StaticInstanceBuildScan.ScanPrefabs(new List<string>());
            }
            else if (strippingRequested)
            {
                Debug.Log(
                    "Static Instances: DOTS variant stripping skipped for this content " +
                    $"build — '{grdAssetName}' enables the GPU Resident Drawer, which needs " +
                    "the DOTS variants of every shader it draws.");
            }

            // The walk runs either way: unarmed it declares nothing, but it is
            // still where a bundled build's fail-fast conditions are found.
            StaticInstanceBuildScan.PreScanScenes(scenePaths, violations);

            if (violations.Count == 0)
            {
                if (StaticInstanceShaderStripping.Active)
                {
                    Debug.Log(
                        "Static Instances: DOTS variant stripping armed for this content build " +
                        $"({StaticInstanceShaderStripping.KeptShaders.Count} shader(s) kept).");
                }

                return null;
            }

            // Refusing means this build strips nothing at all.
            StaticInstanceShaderStripping.Reset();
            var report = new System.Text.StringBuilder(
                "Static Instances: this content build was refused before it started.\n");
            foreach (string violation in violations)
                report.AppendLine($"  {violation}");
            return report.ToString();
        }

        // Call after the bundled build, from a finally. Symmetric with the
        // player build's post-process: report what was stripped, then clear —
        // leaving state armed would let the next operation strip against a
        // keep-set describing content it is not building.
        public static void EndContentBuild()
        {
            if (!StaticInstanceShaderStripping.Active)
            {
                StaticInstanceShaderStripping.Reset();
                return;
            }

            var log = new System.Text.StringBuilder(
                "Static Instances DOTS variant stripping (content build):\n");
            log.AppendLine(
                $"  kept shaders: {StaticInstanceShaderStripping.KeptShaders.Count}, " +
                $"stripped shaders: {StaticInstanceShaderStripping.StrippedShaders.Count}");
            foreach (KeyValuePair<Shader, int> pair in StaticInstanceShaderStripping.StripCounts)
                log.AppendLine($"  stripped {pair.Value} DOTS variants: {pair.Key.name}");
            Debug.Log(log.ToString());

            StaticInstanceShaderStripping.Reset();
        }
    }

    // Implemented by a build script that brackets its build with
    // StaticInstanceBundleStripping's two calls. It carries no members: the
    // point is that a project which integrated the minimal way — two lines in
    // its OWN build script — is still detectable, so the settings page does not
    // tell it to integrate something it already has.
    public interface IStaticInstanceStrippingIntegrated
    {
    }
}
