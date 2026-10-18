using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StaticInstances.Editor
{
    // Build-time auto-bake + source stripping.
    // Runs on the scene copy the build pipeline hands over — the original
    // scene assets are never modified. Every bake target (group or scatter)
    // that is unbaked or stale is baked fresh right here, then gets the
    // three-class strip; the serialized bake in the source scene is only a
    // build-time cache. Validation errors are the one thing that still fails
    // the build fast.
    public sealed class StaticInstanceBuildProcessor : IProcessSceneWithReport
    {
        // Raised once per scene after at least one target was stripped, with
        // that scene's root objects. Tooling outside this assembly that needs
        // to know the scene's sources are gone (they have no MeshRenderers
        // left to draw) subscribes from an [InitializeOnLoadMethod]. Keeping
        // it a hook is what lets Runtime + Editor be copied into another
        // project on their own.
        public static event Action<Scene> SourcesStripped;

        // Optional hook: return true to exempt a target from the auto-bake
        // (it then ships unbaked and collects at runtime).
        // A suppressed target that carries a stale bake still fails the build,
        // because nothing would correct it.
        public static Func<MonoBehaviour, bool> AutoBakeSuppressor;

        public int callbackOrder => 0;

        private static void SetSourcesStripped(StaticInstanceScatter target, bool value)
        {
            var serialized = new SerializedObject(target);
            serialized.FindProperty("bakedSourcesStripped").boolValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            // OnProcessScene also fires when entering Play Mode in the editor,
            // and baking is build-only — but `report == null` is NOT that test.
            // The Scriptable Build Pipeline calls this during a real
            // Addressables content build with a null report, so reading the
            // null as "Play Mode" would skip the bake for every
            // Addressables-delivered scene. `isBuildingPlayer` is the
            // discriminator: true inside any build-pipeline content operation,
            // false when entering Play Mode.
            if (report == null && !BuildPipeline.isBuildingPlayer)
                return;

            // Whether a failure raised from here can actually stop the build.
            // SBP SWALLOWS a BuildFailedException thrown from this
            // callback and reports the content build as successful, so on a
            // bundle path a throw is an exception log that reads like a crash
            // while the build succeeds anyway. A real report means a player or
            // legacy AssetBundle build, which fails properly — that is where
            // the throw stays. The opt-in Addressables build script is what
            // turns a bundle-path violation back into a build failure, by
            // checking before the build rather than from inside it.
            bool canFailBuild = report != null;

            var targets = new List<MonoBehaviour>();
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                targets.AddRange(root.GetComponentsInChildren<StaticInstanceScatter>(true));
            }

            if (targets.Count == 0)
                return;

            // One project-wide decision, read once. A project without the
            // container (non-URP, or stripped by hand) keeps the shipping
            // behaviour.
            bool stripSources =
                !UnityEngine.Rendering.GraphicsSettings.TryGetRenderPipelineSettings(
                    out StaticInstanceStrippingSettings strippingSettings) ||
                strippingSettings.stripBakedSourcesOnBuild;

            // The project-wide half of the same decision AutoBakeSuppressor
            // makes per target. Read once, for the same reason, and
            // defaulting to on when the container is absent.
            bool bakeOnBuild =
                !UnityEngine.Rendering.GraphicsSettings.TryGetRenderPipelineSettings(
                    out StaticInstanceEditorSettings editorSettings) ||
                editorSettings.bakeOnBuild;

            int stripped = 0;
            foreach (MonoBehaviour target in targets)
            {
                var bakeTarget = (IStaticInstanceBakeTarget)target;
                // Always write this on the build copy, never on an authored scene.
                SetSourcesStripped((StaticInstanceScatter)target, false);
                string nestingReason =
                    StaticInstanceBakeUtility.UnsupportedNestingReason(target);
                if (nestingReason != null)
                {
                    Refuse(
                        canFailBuild,
                        $"[SI] {scene.name}/{target.name}: build blocked — {nestingReason}",
                        "unsupported nested SIS content is shipping");
                    continue;
                }

                // The two suppressors compose: either one leaves the target
                // unbaked. They are reported separately because the remedy
                // differs — one is a project setting the user chose, the other
                // an optional hook (AutoBakeSuppressor) they may not know is
                // installed.
                bool suppressedByProject = !bakeOnBuild;
                bool suppressed = suppressedByProject ||
                    AutoBakeSuppressor?.Invoke(target) == true;
                // Ownership-filtered: a parent whose own sources are
                // stripped but which still contains a nested owner's live
                // renderers must read as "pre-stripped", not as "has sources".
                bool hasRenderers =
                    StaticInstanceOwnership.OwnedComponents<MeshRenderer>(target).Length > 0;

                bool invalidMatrices = ((StaticInstanceScatter)target).HasStoredBakedMatrices &&
                    !((StaticInstanceScatter)target).ValidateBakedMatrices();
                if (invalidMatrices && (!hasRenderers || suppressed))
                {
                    Refuse(canFailBuild,
                        $"[SI] {scene.name}/{target.name}: invalid matrix bake cannot be auto-baked. " +
                        "Restore sources and re-bake this owner.", "invalid matrix data is shipping");
                    continue;
                }

                if (!bakeTarget.HasBakedData)
                {
                    if (suppressed)
                    {
                        // Warning, not Log: this is the build shipping content
                        // that will pay a collection walk and an allocation on
                        // load, which is worth seeing in the build log even
                        // when it was asked for.
                        Debug.LogWarning(
                            $"[SI] {scene.name}/{target.name}: {SuppressionReason(suppressedByProject)} — " +
                            "shipping unbaked; the player collects its instances at runtime " +
                            "(managed allocation + load spike) and the source renderers are kept.");
                        continue;
                    }

                    if (!hasRenderers)
                    {
                        Debug.LogWarning(
                            $"[SI] {scene.name}/{target.name}: no baked data and no " +
                            "MeshRenderers — nothing to bake or draw.");
                        continue;
                    }

                    AutoBake(scene, target, bakeTarget, "unbaked", canFailBuild);
                }
                else if (hasRenderers && (invalidMatrices || StaticInstanceBakeUtility.IsBakeStale(target)))
                {
                    // Pre-stripped scenes have no source renderers left;
                    // recomputing the hash there would falsely report stale —
                    // hence the hasRenderers guard.
                    if (suppressed)
                    {
                        // Never a warning-and-continue: staleness is only
                        // detectable with editor APIs, so a stale bake that
                        // reaches a player is drawn as if it were correct
                        // (the owner-matrix guard covers relocation only). Both remedies
                        // are named because either one is a legitimate answer.
                        string staleMessage =
                            $"[SI] {scene.name}/{target.name}: baked data is stale and " +
                            $"{SuppressionReason(suppressedByProject)}, so nothing would " +
                            "correct it — and staleness cannot be detected outside the " +
                            "editor, so the player would draw the stale bake as if it were " +
                            "current. Re-bake this target, or " +
                            (suppressedByProject
                                ? "turn Bake On Build back on in Project Settings > Graphics > Static Instances."
                                : "lift the suppression.");

                        // On a bundle path the condition is reported as loudly as
                        // this callback allows and the target ships with its stale
                        // bake; the opt-in Addressables build script refuses such
                        // a build before it starts.
                        Refuse(canFailBuild, staleMessage, "the stale bake is shipping");
                        continue;
                    }

                    AutoBake(scene, target, bakeTarget, "stale", canFailBuild);
                }

                if (!((StaticInstanceScatter)target).ValidateBakedMatrices())
                {
                    Refuse(canFailBuild,
                        $"[SI] {scene.name}/{target.name}: no valid matrix bake after auto-bake. " +
                        "Sources are kept; re-bake this owner before building.",
                        "invalid matrix data is shipping with sources retained");
                    continue;
                }

                if (!hasRenderers)
                    continue;

                // Project Settings > Graphics > Static Instances: keep the
                // baked sources in the build, e.g. so a comparison build can
                // still draw them as MeshRenderers. The bake above still
                // ran — only the strip is skipped.
                if (!stripSources)
                {
                    Debug.Log(
                        $"[SI] {scene.name}/{target.name}: source strip disabled " +
                        "(Strip Baked Sources On Build off) — baked sources kept in the build.");
                    continue;
                }

                StaticInstanceBakeUtility.StripStats stats =
                    StaticInstanceBakeUtility.StripBakedSources(target);
                SetSourcesStripped((StaticInstanceScatter)target, true);
                stripped++;
                Debug.Log($"[SI] {scene.name}/{target.name}: stripped baked sources ({stats}).");
            }

            // Raised only when something was actually stripped: subscribers
            // use it to learn that the scene's sources are gone, which is
            // wrong for a build that kept them.
            if (stripped == 0)
                return;

            SourcesStripped?.Invoke(scene);
        }

        // The suppression decision, readable from outside the callback.
        // A bundled build's fail-fast has to be made BEFORE the build — SBP
        // swallows a throw from inside it — and that check needs the same
        // answer this processor would reach: would the auto-bake run for this
        // target, or is something suppressing it?
        internal static bool WillAutoBake(MonoBehaviour target, out string suppressionReason)
        {
            bool bakeOnBuild =
                !UnityEngine.Rendering.GraphicsSettings.TryGetRenderPipelineSettings(
                    out StaticInstanceEditorSettings editorSettings) ||
                editorSettings.bakeOnBuild;

            if (!bakeOnBuild)
            {
                suppressionReason = SuppressionReason(byProject: true);
                return false;
            }

            if (AutoBakeSuppressor?.Invoke(target) == true)
            {
                suppressionReason = SuppressionReason(byProject: false);
                return false;
            }

            suppressionReason = null;
            return true;
        }

        // Which switch left this target unbaked. Named in every message that
        // reports the state, because the two have different remedies and the
        // optional AutoBakeSuppressor hook is invisible to a user who did not
        // install it.
        private static string SuppressionReason(bool byProject)
        {
            return byProject
                ? "the build-time bake is off (Project Settings > Graphics > " +
                  "Static Instances > Bake On Build)"
                : "the auto-bake is suppressed for this target";
        }

        // Bakes the target in place on the scene copy. Validation errors fail
        // the build — they are the same conditions that would have made a
        // manual bake refuse to run — EXCEPT on a path that cannot be failed
        // from here, where they are reported as errors instead (SBP
        // swallows a BuildFailedException raised from this callback and reports
        // the content build as successful, so throwing would abandon this
        // scene's remaining targets mid-loop and leave them unbaked and
        // un-stripped, silently, beside a log entry that reads like a crash).
        private static void AutoBake(
            Scene scene,
            MonoBehaviour target,
            IStaticInstanceBakeTarget bakeTarget,
            string reason,
            bool canFailBuild)
        {
            // The second of the two Editor-side bake gates. In a
            // build this should be unreachable — the targets come from a
            // scene copy — but it is here because the rule is "no bake in a
            // prefab context" and a rule enforced at one of its two callers
            // is a rule that drifts.
            string refusal = StaticInstanceBakeUtility.PrefabBakeRefusalReason(target);
            if (refusal != null)
            {
                Debug.LogWarning(
                    $"[SI] {scene.name}/{target.name}: auto-bake skipped because " +
                    $"{refusal}.", target);
                return;
            }

            List<StaticInstanceBakeUtility.Issue> issues =
                StaticInstanceBakeUtility.Validate(target);
            foreach (StaticInstanceBakeUtility.Issue issue in issues)
            {
                if (!StaticInstanceBakeUtility.IsBlockingError(issue))
                    continue;

                Refuse(
                    canFailBuild,
                    $"[SI] {scene.name}/{target.name}: auto-bake blocked by a " +
                    $"validation error — {issue.Message}",
                    "this target is shipping unbaked");
                return;
            }

            if (!bakeTarget.BakeFromHierarchy())
            {
                Refuse(
                    canFailBuild,
                    $"[SI] {scene.name}/{target.name}: auto-bake found no valid " +
                    "MeshRenderers.",
                    "this target is shipping unbaked");
                return;
            }

            bakeTarget.ApplyBakedSourceHash(
                StaticInstanceBakeUtility.ComputeSourceHash(target));
            Debug.Log($"[SI] {scene.name}/{target.name}: auto-baked ({reason}) on the scene copy.");
        }

        // One place for the "stop the build, or say so as loudly as this path
        // allows" decision, so the three conditions that can refuse a bake
        // cannot drift apart on it. The bundled-path message names the
        // remedy that does refuse such a build.
        internal static void Refuse(bool canFailBuild, string message, string bundledConsequence)
        {
            if (canFailBuild)
                throw new BuildFailedException(message);

            Debug.LogError(
                message +
                " This is a bundled content build, which cannot be failed from a scene " +
                $"processor — {bundledConsequence}. Set the Static Instances build script " +
                "on your Addressables settings to have such a build refused instead.");
        }
    }
}
