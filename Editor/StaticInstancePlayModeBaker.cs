using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace StaticInstances.Editor
{
    // Bakes every unbaked or stale target in the loaded scenes right before
    // Play Mode starts. ExitingEditMode still runs in
    // edit mode, so the result persists in the scene — that is what makes
    // the inspector's baked instance count appear without a build — and Play
    // then starts from the same baked state a build would (no runtime
    // collection walk, no "collected at runtime" warning).
    //
    // Mirrors StaticInstanceBuildProcessor's gating: fresh bakes are skipped
    // (an unchanged scene enters Play with zero extra cost and no dirty
    // flag), the AutoBakeSuppressor hook is honored (a suppressed target
    // keeps runtime collection), and validation errors skip the
    // target. Unlike a build, an error does not block Play — the target just
    // stays on the runtime-collection fallback.
    //
    // The scene is left dirty rather than saved: baking rewrites serialized
    // arrays, and whether that lands in version control is the user's call.
    // A workflow that would rather Play Mode never touched its scenes turns
    // the whole thing off in Project Settings > Graphics > Static Instances
    // ("Bake On Entering Play Mode", default on). That switch is deliberately
    // separate from the build-time auto-bake: this one only changes what
    // pressing Play does, never what a build contains.
    [InitializeOnLoad]
    internal static class StaticInstancePlayModeBaker
    {
        static StaticInstancePlayModeBaker()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.ExitingEditMode)
                return;

            // Project Settings > Graphics > Static Instances. Default on;
            // off is for workflows that would rather Play Mode never touched
            // their scenes. Deliberately independent of the build-time auto
            // bake, and of the AutoBakeSuppressor hook below — those
            // change what a build contains, this only changes what pressing
            // Play does. A project without the container (non-URP, or the
            // settings stripped by hand) keeps baking.
            if (GraphicsSettings.TryGetRenderPipelineSettings(
                    out StaticInstanceEditorSettings editorSettings) &&
                !editorSettings.bakeOnEnteringPlayMode)
            {
                return;
            }

            int baked = 0;
            foreach (MonoBehaviour target in
                StaticInstanceBakeUtility.FindBakeTargetsInScene())
            {
                var bakeTarget = (IStaticInstanceBakeTarget)target;
                if (StaticInstanceBuildProcessor.AutoBakeSuppressor?.Invoke(target) == true)
                    continue;

                // Pre-stripped content (no source renderers left) can neither
                // be baked nor go stale — recomputing the hash there would
                // falsely report stale, same guard as the build processor.
                bool hasRenderers =
                    StaticInstanceOwnership.OwnedComponents<MeshRenderer>(target).Length > 0;
                if (!hasRenderers)
                    continue;

                bool needsBake = !bakeTarget.HasBakedData ||
                    StaticInstanceBakeUtility.IsBakeStale(target);
                if (!needsBake)
                    continue;

                List<StaticInstanceBakeUtility.Issue> issues =
                    StaticInstanceBakeUtility.Validate(target);
                StaticInstanceBakeUtility.LogIssues(target, issues);
                if (issues.Exists(StaticInstanceBakeUtility.IsBlockingError))
                {
                    Debug.LogError(
                        $"[SI] {target.name}: play-mode bake skipped by validation " +
                        "errors — entering Play on the runtime-collection fallback.",
                        target);
                    continue;
                }

                if (StaticInstanceBakeUtility.BakeGroup(target))
                    baked++;
            }

            if (baked > 0)
            {
                Debug.Log(
                    $"[SI] Play-mode bake: {baked} target(s) baked before entering " +
                    "Play Mode (scene left dirty — save to persist).");
            }
        }
    }
}
