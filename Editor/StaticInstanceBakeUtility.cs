using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace StaticInstances.Editor
{
    // Editor core of the bake pipeline:
    // - source hash: deterministic digest of the renderer hierarchy so a
    //   stale bake is detected at validate/build time (never auto re-baked),
    // - validation: errors/warnings/info for the collected sources,
    // - three-class strip: render-only GameObjects are deleted, collider
    //   carriers survive with their renderer components removed and are
    //   flattened next to their nearest surviving ancestor, anything with
    //   other components is left untouched.
    // Shared by the inspector, the build processor and any external
    // tooling, so every consumer strips with identical rules.
    // Assigns the runtime-visible stale check on every domain reload, so
    // editor Play can ignore an outdated bake and fall back to runtime
    // collection (StaticInstanceEditorBridge in the runtime assembly).
    [InitializeOnLoad]
    internal static class StaticInstanceEditorBridgeInstaller
    {
        static StaticInstanceEditorBridgeInstaller()
        {
            StaticInstanceEditorBridge.IsBakeStale = StaticInstanceBakeUtility.IsBakeStale;
            StaticInstanceEditorBridge.AssignStaticMaterials =
                StaticInstanceWindStaticMaterials.AssignExistingClones;
        }
    }

    public static class StaticInstanceBakeUtility
    {
        // Serialized bake format version. It is part of the source hash, so
        // changing it makes every existing bake stale. `bakeOnBuild` (default
        // on) re-bakes stale data at build; with it off, a stale bake fails the
        // build. The format stores per-renderer shadow mode, layer, winding
        // flip and rendering layer mask; the owner's world matrix at bake time,
        // so a relocated bake is detectable in a player build where the
        // editor-only stale check does not exist; and a per-instance shape
        // factor that corrects the bounding-sphere size input. The shape factor
        // is baked because it needs the mesh's local extents, while only the
        // world AABB is serialized per instance; a bake without shape factors
        // reads as identity. Groups whose material enables a wind keyword draw
        // a wind-stripped static clone.
        public const int BakeFormatVersion = 1;

        public struct StripStats
        {
            public int RemovedGameObjects;
            public int StrippedRendererComponents;
            public int KeptColliderObjects;
            public int KeptOtherObjects;
            public int KeptTransformAncestors;

            public override string ToString()
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "removed {0} GameObjects, stripped {1} renderer components, " +
                    "kept {2} collider objects, {3} scripted objects, {4} transform ancestors",
                    RemovedGameObjects,
                    StrippedRendererComponents,
                    KeptColliderObjects,
                    KeptOtherObjects,
                    KeptTransformAncestors);
            }
        }

        public readonly struct Issue
        {
            public readonly LogType Severity;
            public readonly string Message;

            // The objects the finding is about, so a UI can list and ping
            // them. Never null.
            public readonly UnityEngine.Object[] Targets;

            public Issue(LogType severity, string message)
                : this(severity, message, null)
            {
            }

            public Issue(LogType severity, string message, IEnumerable<UnityEngine.Object> targets)
            {
                Severity = severity;
                Message = message;
                Targets = targets != null
                    ? new List<UnityEngine.Object>(targets).ToArray()
                    : Array.Empty<UnityEngine.Object>();
            }
        }

        // One target's validation findings, as the Static Instances window
        // keeps them after a Validate press.
        public readonly struct ValidationResult
        {
            public readonly MonoBehaviour Group;
            public readonly List<Issue> Issues;

            public ValidationResult(MonoBehaviour group, List<Issue> issues)
            {
                Group = group;
                Issues = issues;
            }
        }

        // A bake taken inside a prefab asset or a prefab stage is
        // never valid. Instance matrices are WORLD-space, so the data is
        // correct only at the transform it was taken at, and a prefab has no
        // place in the world until it is instantiated, whereupon the
        // owner-matrix guard rejects the bake and falls back to runtime
        // collection. Baking there produces dead weight that inflates the
        // asset and is never used.
        //
        // Returns the refusal reason, or null when the target may be baked.
        //
        // `GetPrefabStage(gameObject)`, deliberately NOT `GetCurrentPrefabStage()`:
        // the latter asks "is any prefab stage open", which would refuse to
        // bake ordinary SCENE objects whenever a prefab happens to be open,
        // and `bakeOnBuild` and the play-mode baker both bake scene objects.
        public static string PrefabBakeRefusalReason(MonoBehaviour group)
        {
            if (group == null)
                return null;

            if (PrefabUtility.IsPartOfPrefabAsset(group))
            {
                return "it lives in a prefab asset, and a bake is only valid at " +
                    "the transform it was taken at";
            }

            if (UnityEditor.SceneManagement.PrefabStageUtility.GetPrefabStage(
                    group.gameObject) != null)
            {
                return "it is open in a prefab stage, and a bake is only valid at " +
                    "the transform it was taken at";
            }

            return null;
        }

        // Bakes the target's sources into its serialized arrays and stores the
        // source hash. The caller owns persistence (SetDirty / scene save).
        // Targets are StaticInstanceScatter components implementing
        // IStaticInstanceBakeTarget.
        //
        // This is one of the two Editor-side gates against baking in a prefab — it covers
        // the Inspector button and the play-mode baker; the build's auto-bake
        // is the other (StaticInstanceBuildProcessor). The gate sits here
        // rather than in IStaticInstanceBakeTarget.BakeFromHierarchy because
        // that interface is implemented in the RUNTIME assembly, and the
        // prefab APIs above are editor-only. The residue is a script calling
        // BakeFromHierarchy() directly at a prefab asset; that degrades
        // safely rather than silently — the owner-matrix guard catches it at runtime and
        // the pre-build scan reports it at build time.
        public static bool BakeGroup(MonoBehaviour group)
        {
            string refusal = PrefabBakeRefusalReason(group);
            if (refusal != null)
            {
                Debug.LogWarning(
                    $"[SI] {group.name}: bake skipped because {refusal}. Bake the " +
                    "scene instance instead — that is the intended workflow " +
                    "(prefabs carrying Static Instances are a development-time " +
                    "fallback).",
                    group);
                return false;
            }

            var target = (IStaticInstanceBakeTarget)group;
            if (!target.BakeFromHierarchy())
                return false;
            target.ApplyBakedSourceHash(ComputeSourceHash(group));
            EditorUtility.SetDirty(group);
            return true;
        }

        public static bool IsBakeStale(MonoBehaviour group)
        {
            var target = (IStaticInstanceBakeTarget)group;
            if (!target.HasBakedData)
                return false;
            return target.BakedSourceHash != ComputeSourceHash(group);
        }

        // Deterministic digest of everything the bake serializes from the
        // hierarchy: per collected renderer (same filter and order as
        // CollectFromHierarchy) the relative path, world matrix, mesh and
        // material asset identities, shadow mode and layer, plus the format
        // version. Asset identities use GUID + local file id, so re-imports
        // do not shift the hash but swapping the asset does.
        public static string ComputeSourceHash(MonoBehaviour group)
        {
            var builder = new StringBuilder(1024 * 64);
            builder.Append("v").Append(BakeFormatVersion).Append('\n');

            // Same ownership boundary as collection, for the reason the
            // hash exists: it has to describe exactly the set that was baked,
            // or a nested child's edit stales the parent's bake and vice
            // versa.
            foreach (MeshRenderer renderer in
                StaticInstanceOwnership.OwnedComponents<MeshRenderer>(group))
            {
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                Mesh mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                    continue;

                AppendPath(builder, renderer.transform, group.transform);
                AppendMatrix(builder, renderer.localToWorldMatrix);
                AppendAssetId(builder, mesh);
                foreach (Material material in renderer.sharedMaterials)
                    AppendAssetId(builder, material);
                builder.Append((int)renderer.shadowCastingMode).Append('|');
                builder.Append(renderer.gameObject.layer).Append('|');
                builder.Append(renderer.renderingLayerMask).Append('\n');
            }

            return Hash128.Compute(builder.ToString()).ToString();
        }

        // Does this target carry baked data it can never use,
        // because it lives in a prefab rather than in a scene? Shared by
        // Validate, the inspector band and the pre-build scan so all three
        // agree on what "a baked prefab" is.
        public static bool CarriesBakedDataInPrefab(MonoBehaviour group)
        {
            return group != null &&
                ((IStaticInstanceBakeTarget)group).HasBakedData &&
                PrefabBakeRefusalReason(group) != null;
        }

        internal static string UnsupportedNestingReason(MonoBehaviour owner)
        {
            Component nested = StaticInstanceOwnership.FindUnsupportedNesting(owner);
            if (nested == null)
                return null;

            return $"'{GetPath(nested.transform, owner.transform)}' is a nested " +
                "StaticInstanceScatter. Nested SIS owners are not supported; " +
                "split the content into separate, non-nested hierarchies.";
        }

        public static List<Issue> Validate(MonoBehaviour group, bool includeStaleCheck = true)
            => ValidateCore(group, includeStaleCheck, includeOwnerMatrixCheck: true);

        // Validation for the current URP material workflow. Returns issues;
        // LogType.Error entries should block a bake.
        // `includeStaleCheck` exists for CountBlockingErrors alone: the stale
        // issue is the one Error IsBlockingError always rejects, and producing
        // it means hashing the whole hierarchy, which costs much more than the
        // rest of validation. Every caller that reports to a human leaves it on.
        // Source diagnostics used immediately before a fresh scene bake. Existing
        // cache state is repaired by that bake; public validation keeps reporting it.
        internal static List<Issue> ValidateForBake(MonoBehaviour group)
            => ValidateCore(group, includeStaleCheck: false, includeOwnerMatrixCheck: false);

        private static List<Issue> ValidateCore(MonoBehaviour group,
            bool includeStaleCheck, bool includeOwnerMatrixCheck)
        {
            var issues = new List<Issue>();

            // A Warning, not an Error: the data is inert rather
            // than wrong — the owner-matrix guard rejects it at Initialize
            // and the runtime-collection fallback draws the prefab correctly
            // wherever it is instantiated. What it costs is asset size and a
            // misleading "this is baked" reading in the inspector. Reporting
            // with a one-click Clear rather than stripping on save follows one
            // rule: tell the author and offer the fix, never discard
            // authored data behind their back.
            if (CarriesBakedDataInPrefab(group))
            {
                issues.Add(new Issue(
                    LogType.Warning,
                    "This prefab carries baked instance data, which it can never use: " +
                    "the matrices are world-space, so they are valid only at the " +
                    "transform they were baked at, and an instantiated prefab falls " +
                    "back to runtime collection anyway. Clear it — the data is dead " +
                    "weight in the asset. Baking is against the scene by design.",
                    new UnityEngine.Object[] { group }));
            }

            string nestingReason = UnsupportedNestingReason(group);
            if (nestingReason != null)
            {
                issues.Add(new Issue(
                    LogType.Error,
                    nestingReason,
                    new UnityEngine.Object[] { StaticInstanceOwnership.FindUnsupportedNesting(group) }));
            }

            LODGroup[] lodGroups = StaticInstanceOwnership.OwnedComponents<LODGroup>(group);
            foreach (LODGroup lodGroup in lodGroups)
            {
                issues.Add(new Issue(
                    LogType.Error,
                    $"LODGroup '{GetPath(lodGroup.transform, group.transform)}' is not supported under a static-instance component.",
                    new UnityEngine.Object[] { lodGroup }));
            }

            var skipped = new List<UnityEngine.Object>();
            var sheared = new List<UnityEngine.Object>();
            var negative = new List<UnityEngine.Object>();
            int shadowsOff = 0;
            int shadowsOnly = 0;
            int twoSided = 0;
            var uniqueLayers = new HashSet<int>();
            var otherObjects = new List<string>();
            var otherTargets = new List<UnityEngine.Object>();
            var colliderObjects = new List<UnityEngine.Object>();
            int triggerColliders = 0;

            foreach (MeshRenderer renderer in
                StaticInstanceOwnership.OwnedComponents<MeshRenderer>(group))
            {
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                Mesh mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                {
                    skipped.Add(renderer);
                    continue;
                }

                var rendererTarget = new UnityEngine.Object[] { renderer };

                // Warning, not Error: what is at stake is a changed
                // appearance (there is no per-instance material-property
                // channel, so the variation is lost), which is the
                // transparent-material check's class, not a broken bake. A
                // block is draw-time state that is never serialized, so as an
                // Error it would block bakes depending on session state
                // rather than on the scene.
                if (renderer.HasPropertyBlock())
                {
                    issues.Add(new Issue(
                        LogType.Warning,
                        $"'{GetPath(renderer.transform, group.transform)}' has a " +
                        "MaterialPropertyBlock. Static Instances has no per-instance " +
                        "material-property channel, so that variation is lost and the " +
                        "instance draws with the material's own values. The block is not " +
                        "serialized, so this warning disappears when the scene is reopened " +
                        "and returns whenever whatever sets it runs again.",
                        rendererTarget));
                }

                StaticEditorFlags flags =
                    GameObjectUtility.GetStaticEditorFlags(renderer.gameObject);
                if ((flags & StaticEditorFlags.BatchingStatic) != 0)
                {
                    issues.Add(new Issue(
                        LogType.Error,
                        $"'{GetPath(renderer.transform, group.transform)}' has BatchingStatic enabled (must be off for BRG sources).",
                        rendererTarget));
                }

                foreach (Material material in renderer.sharedMaterials)
                {
                    if (material == null)
                    {
                        issues.Add(new Issue(
                            LogType.Error,
                            $"'{GetPath(renderer.transform, group.transform)}' has a null material.",
                            rendererTarget));
                    }
                    else if (IsTransparentMaterial(material))
                    {
                        issues.Add(new Issue(
                            LogType.Warning,
                            $"'{GetPath(renderer.transform, group.transform)}' uses transparent material " +
                            $"'{material.name}' (unsupported by design; it renders opaque).",
                            rendererTarget));
                    }
                }

                switch (renderer.shadowCastingMode)
                {
                    case ShadowCastingMode.Off:
                        shadowsOff++;
                        break;
                    case ShadowCastingMode.ShadowsOnly:
                        shadowsOnly++;
                        break;
                    case ShadowCastingMode.TwoSided:
                        twoSided++;
                        break;
                }

                uniqueLayers.Add(renderer.gameObject.layer);

                if (HasShear(renderer.localToWorldMatrix))
                    sheared.Add(renderer);
                if (renderer.localToWorldMatrix.determinant < 0f)
                    negative.Add(renderer);
            }

            foreach (Transform child in StaticInstanceOwnership.OwnedComponents<Transform>(group))
            {
                if (child == group.transform)
                    continue;
                switch (Classify(child.gameObject))
                {
                    case NodeClass.PhysicsSurvivor:
                        colliderObjects.Add(child.gameObject);
                        foreach (Collider collider in child.GetComponents<Collider>())
                        {
                            if (collider.isTrigger)
                                triggerColliders++;
                        }

                        break;
                    case NodeClass.Other:
                        otherObjects.Add(GetPath(child, group.transform));
                        otherTargets.Add(child.gameObject);
                        break;
                }
            }

            if (skipped.Count > 0)
            {
                issues.Add(new Issue(
                    LogType.Warning,
                    $"{skipped.Count} renderers were skipped (missing mesh, disabled or inactive) and are not part of the bake.",
                    skipped));
            }

            if (sheared.Count > 0)
            {
                issues.Add(new Issue(
                    LogType.Warning,
                    $"{sheared.Count} sources have a sheared transform (a non-uniformly scaled " +
                    "ancestor with a rotated child). Rendering and culling bounds stay exact; only " +
                    "the Size Cull / Screen Size LOD shape factor is approximated, on the " +
                    "conservative side (the instance draws slightly farther than intended). Move " +
                    "the non-uniform scale off the ancestor to remove the shear.",
                    sheared));
            }
            if (negative.Count > 0)
                issues.Add(new Issue(LogType.Warning, $"{negative.Count} sources have negative scale (winding is flipped).", negative));

            if (shadowsOff + shadowsOnly + twoSided > 0)
            {
                issues.Add(new Issue(
                    LogType.Log,
                    $"Per-renderer shadow modes: {shadowsOff} Off, {shadowsOnly} ShadowsOnly, {twoSided} TwoSided " +
                    "(honored per draw group)."));
            }

            if (uniqueLayers.Count > 1)
            {
                issues.Add(new Issue(
                    LogType.Log,
                    $"Sources span {uniqueLayers.Count} layers; draw groups split per layer (preserved per renderer)."));
            }

            // Additive over the hash rather than redundant with it. The
            // hash digests renderer WORLD matrices, so moving an owner that
            // still has sources changes it and the stale check fires first —
            // but once the sources are stripped the hash covers nothing that
            // moves, and only this catches the relocation.
            var guardTarget = (IStaticInstanceBakeTarget)group;
            if (includeOwnerMatrixCheck && guardTarget.HasBakedData &&
                !StaticInstanceBakeGuard.MatchesOwner(
                    guardTarget.BakedOwnerMatrix, group.transform))
            {
                issues.Add(new Issue(
                    LogType.Error,
                    "Baked at a different transform: the baked matrices are world-space, so " +
                    "they no longer describe where this sits. Re-bake it here. (At runtime it " +
                    "falls back to collecting its sources, and draws nothing if they are gone.)",
                    new UnityEngine.Object[] { group }));
            }

            if (includeStaleCheck &&
                ((IStaticInstanceBakeTarget)group).HasBakedData && IsBakeStale(group))
            {
                issues.Add(new Issue(
                    LogType.Error,
                    "Baked data is stale: the hierarchy changed after the last bake. Re-run Bake.",
                    new UnityEngine.Object[] { group }));
            }

            if (colliderObjects.Count > 0)
            {
                issues.Add(new Issue(
                    LogType.Log,
                    $"{colliderObjects.Count} collider objects survive a strip build with their renderers removed ({triggerColliders} triggers).",
                    colliderObjects));
            }

            if (otherObjects.Count > 0)
            {
                issues.Add(new Issue(
                    LogType.Log,
                    $"{otherObjects.Count} objects carry other components and are left untouched by a strip build: " +
                    string.Join(", ", otherObjects),
                    otherTargets));
            }

            return issues;
        }

        // Transparent surfaces are unsupported by design: BRG
        // draws them opaque. URP Lit stores
        // the surface type in _Surface (1 = Transparent); the keyword and
        // render queue cover custom shaders.
        internal static bool IsTransparentMaterial(Material material)
        {
            if (material.HasProperty("_Surface") && material.GetFloat("_Surface") > 0.5f)
                return true;
            if (material.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT"))
                return true;
            return material.renderQueue >= (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }

        // Shear: the transform's axes are no longer perpendicular, which only
        // a non-uniformly scaled ancestor over a rotated descendant produces.
        //
        // Non-uniform scale by itself is fully supported — the bake keeps the
        // whole localToWorldMatrix, uploads its exact inverse as
        // unity_WorldToObject (so URP's normal transform is correct) and
        // culls with Renderer.bounds. Shear is the one case that degrades
        // anything:
        // StaticInstanceBuilder.ShapeFactorOf reads the column lengths as
        // box edges, which assumes perpendicular axes, and errs toward a
        // larger factor. lossyScale cannot detect it (it approximates exactly
        // the skew this looks for), so the matrix columns are tested directly.
        //
        // 1e-3 on the normalized dot product is ~0.06° off perpendicular: far
        // above the float noise of composed TRS (~1e-7, a 90° child under a
        // 2:1 parent included), and a 2:1 parent trips it from ~0.04° of
        // child rotation. A zero-length axis is not shear (and ShapeFactorOf
        // already reads it as identity).
        internal static bool HasShear(in Matrix4x4 matrix)
        {
            Vector3 x = matrix.GetColumn(0);
            Vector3 y = matrix.GetColumn(1);
            Vector3 z = matrix.GetColumn(2);
            if (x.sqrMagnitude < 1e-12f || y.sqrMagnitude < 1e-12f || z.sqrMagnitude < 1e-12f)
                return false;

            x.Normalize();
            y.Normalize();
            z.Normalize();
            const float threshold = 1e-3f;
            return Mathf.Abs(Vector3.Dot(x, y)) > threshold ||
                   Mathf.Abs(Vector3.Dot(y, z)) > threshold ||
                   Mathf.Abs(Vector3.Dot(z, x)) > threshold;
        }

        // Whether an issue is one that actually stops a bake. Errors block,
        // with exactly one exception: staleness is what the bake is about to
        // fix, so neither auto-bake path treats it as blocking.
        //
        // Single source for this predicate: the play-mode baker, the build
        // processor and the inspector's status line must agree exactly.
        internal static bool IsBlockingError(Issue issue)
        {
            return issue.Severity == LogType.Error &&
                !issue.Message.StartsWith("Baked data is stale", StringComparison.Ordinal);
        }

        // How many validation errors would stop this target's bake. Walks the
        // whole hierarchy (Validate does), so per-repaint callers cache it.
        internal static int CountBlockingErrors(MonoBehaviour target)
        {
            int blocking = 0;
            foreach (Issue issue in Validate(target, includeStaleCheck: false))
            {
                if (IsBlockingError(issue))
                    blocking++;
            }

            return blocking;
        }

        public static void LogIssues(MonoBehaviour group, List<Issue> issues)
        {
            if (issues.Count == 0)
            {
                Debug.Log($"'{group.name}': validation passed with no findings.", group);
                return;
            }

            foreach (Issue issue in issues)
            {
                string message = $"'{group.name}': {issue.Message}";
                switch (issue.Severity)
                {
                    case LogType.Error:
                        Debug.LogError(message, group);
                        break;
                    case LogType.Warning:
                        Debug.LogWarning(message, group);
                        break;
                    default:
                        Debug.Log(message, group);
                        break;
                }
            }
        }

        // ---------------------------------------------------------------
        // Three-class strip. Post-order over the group's
        // children; the group root itself is never touched.
        // ---------------------------------------------------------------

        private enum NodeClass
        {
            RenderOnly,
            PhysicsSurvivor,
            Other,
        }

        public static StripStats StripBakedSources(MonoBehaviour group)
        {
            GameObject root = group.gameObject;
            UnpackPrefabInstances(root);

            var stats = new StripStats();
            for (int i = root.transform.childCount - 1; i >= 0; i--)
                Process(root.transform.GetChild(i), root.transform, ref stats);

            return stats;
        }

        private static void Process(
            Transform node, Transform reparentTarget, ref StripStats stats)
        {
            // A nested Static Instance component owns its own subtree
            // and runs its own strip as a separate bake target, so this one
            // stops here. Classify() reports the nested owner's own node as
            // "Other" (it carries a component that is neither renderer nor
            // collider), and "Other" still recurses into the children, which
            // would delete the nested owner's sources.
            if (StaticInstanceOwnership.IsOwner(node.gameObject))
            {
                stats.KeptOtherObjects++;
                return;
            }

            NodeClass nodeClass = Classify(node.gameObject);

            // Children of an untouched ("other") node stay under it; children
            // of stripped nodes get flattened to the nearest surviving
            // ancestor instead.
            Transform childTarget = nodeClass == NodeClass.Other ? node : reparentTarget;
            for (int i = node.childCount - 1; i >= 0; i--)
                Process(node.GetChild(i), childTarget, ref stats);

            if (nodeClass == NodeClass.Other)
            {
                stats.KeptOtherObjects++;
                return;
            }

            // The renderer components were baked; remove them from both the
            // render-only and the collider-carrying classes.
            foreach (MeshRenderer renderer in node.GetComponents<MeshRenderer>())
            {
                UnityEngine.Object.DestroyImmediate(renderer);
                stats.StrippedRendererComponents++;
            }

            foreach (MeshFilter filter in node.GetComponents<MeshFilter>())
                UnityEngine.Object.DestroyImmediate(filter);

            if (nodeClass == NodeClass.PhysicsSurvivor)
            {
                // Flatten: world pose is preserved, the pure-Transform
                // chain above becomes deletable.
                if (node.parent != reparentTarget)
                {
                    // SetParent keeps the world pose but not the inherited
                    // inactive state: a self-active collider under an
                    // inactive render-only ancestor would come back to life
                    // in builds. Deactivate the node itself to keep its
                    // effective state.
                    bool wasActive = node.gameObject.activeInHierarchy;
                    node.SetParent(reparentTarget, worldPositionStays: true);
                    if (!wasActive && node.gameObject.activeInHierarchy)
                        node.gameObject.SetActive(false);
                }
                stats.KeptColliderObjects++;
                return;
            }

            if (node.childCount == 0)
            {
                UnityEngine.Object.DestroyImmediate(node.gameObject);
                stats.RemovedGameObjects++;
            }
            else
            {
                // A surviving "other" descendant pins this pure Transform.
                stats.KeptTransformAncestors++;
            }
        }

        private static NodeClass Classify(GameObject gameObject)
        {
            bool hasCollider = false;
            foreach (Component component in gameObject.GetComponents<Component>())
            {
                if (component is Transform || component is MeshFilter || component is MeshRenderer)
                    continue;
                if (component is Collider)
                {
                    hasCollider = true;
                    continue;
                }

                return NodeClass.Other;
            }

            return hasCollider ? NodeClass.PhysicsSurvivor : NodeClass.RenderOnly;
        }

        // Scene-only hierarchies can be edited freely; prefab instances must
        // be fully unpacked first.
        public static void UnpackPrefabInstances(GameObject root)
        {
            for (int guard = 0; guard < 64; guard++)
            {
                GameObject packed = null;
                if (PrefabUtility.IsPartOfPrefabInstance(root))
                {
                    packed = PrefabUtility.GetOutermostPrefabInstanceRoot(root);
                }
                else
                {
                    foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
                    {
                        if (PrefabUtility.IsPartOfPrefabInstance(child.gameObject))
                        {
                            packed = PrefabUtility.GetOutermostPrefabInstanceRoot(child.gameObject);
                            break;
                        }
                    }
                }

                if (packed == null)
                    return;
                PrefabUtility.UnpackPrefabInstance(
                    packed, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            }

            throw new InvalidOperationException("Prefab unpacking did not converge.");
        }

        // Every SIS bake target in the loaded scenes.
        public static List<MonoBehaviour> FindBakeTargetsInScene()
        {
            var targets = new List<MonoBehaviour>();
#if UNITY_6000_4_OR_NEWER
            // 6000.4 deprecated FindObjectsSortMode. No caller depends on the
            // order: every target is baked, validated or cleared on its own.
            targets.AddRange(UnityEngine.Object.FindObjectsByType<StaticInstanceScatter>(
                FindObjectsInactive.Include));
#else
            targets.AddRange(UnityEngine.Object.FindObjectsByType<StaticInstanceScatter>(
                FindObjectsInactive.Include, FindObjectsSortMode.InstanceID));
#endif
            return targets;
        }

        // Manual batch bake. Builds bake automatically (the build processor
        // re-bakes anything unbaked or stale on the scene copy), so this is
        // for pinning the serialized cache — e.g. to keep large scenes from
        // paying the collection walk on every build.
        // The UI entry point is the "Current Scene" section of Project
        // Settings > Graphics > Static Instances; this stays public static for
        // editor automation.
        public static void BakeAllInScene()
        {
            Scene scene = SceneManager.GetActiveScene();
            int baked = 0;
            foreach (MonoBehaviour group in FindBakeTargetsInScene())
            {
                List<Issue> issues = Validate(group);
                LogIssues(group, issues);
                if (issues.Exists(IsBlockingError))
                {
                    Debug.LogError($"'{group.name}': bake aborted by validation errors.", group);
                    continue;
                }

                if (BakeGroup(group))
                    baked++;
            }

            // The wind-stripped variants are generated here rather than
            // inside the bake itself, because generating them writes assets and the
            // build-time bake runs inside scene processing. Attaching it to the
            // explicit press keeps asset creation an editor-side act. Before the
            // save, so a "scene saved" message is true of the draw groups it just
            // rewrote.
            StaticInstanceWindStaticMaterials.GenerateForOpenScenes();

            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
            Debug.Log($"Source bake: {baked} targets baked, scene saved.");
        }

        /// <summary>
        /// Clear every Static Instance bake in the active scene, and collect the
        /// wind-stripped material clones that scene was keeping alive.
        ///
        /// Clear is the clone GC's trigger: it already means "remove everything derived
        /// from this scene", so the generated materials belong to it.
        ///
        /// Bake data first, then the SAVE, then the clones — and that order is the unit
        /// working or not. The clones are referenced by the bake data, so clearing it is
        /// what makes them collectable; but the dependency check that guards the deletion
        /// reads the asset ON DISK, so collecting before the save finds every clone still
        /// referenced and keeps all of them.
        ///
        /// **Scoped to the ACTIVE scene**, unlike its neighbours in that settings section:
        /// `FindBakeTargetsInScene` spans every loaded scene, which is right for baking and
        /// wrong for the one destructive action of the four.
        /// </summary>
        public static void ClearAllInScene()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid())
                return;

            string sceneGuid = string.IsNullOrEmpty(scene.path)
                ? null
                : AssetDatabase.AssetPathToGUID(scene.path);

            int cleared = 0;
            int skippedOtherScenes = 0;
            foreach (MonoBehaviour target in FindBakeTargetsInScene())
            {
                // ACTIVE SCENE ONLY: `FindBakeTargetsInScene` spans every
                // LOADED scene despite its name, so without this filter the
                // action would destroy bake data in additively loaded scenes
                // while saving, reporting and collecting for just this one.
                // This is the only DESTRUCTIVE operation in the section, so it
                // is scoped to what its own name, dialog and log all say: a
                // re-bake is cheap, an unnoticed loss is not.
                if (target.gameObject.scene != scene)
                {
                    skippedOtherScenes++;
                    continue;
                }

                Undo.RecordObject(target, "Clear Baked Data");
                ((IStaticInstanceBakeTarget)target).ClearBakedData();
                EditorUtility.SetDirty(target);
                cleared++;
            }

            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
            bool saved = UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);

            // COLLECT AFTER THE SAVE. The dependency check reads the IMPORTED
            // scene asset, so collecting first would find the scene on disk
            // still referencing every clone and veto all of them.
            //
            // The save is also checked: excluding this scene from the veto is
            // only safe while the scene ON DISK no longer references the
            // clones, i.e. only if the save actually happened. A read-only
            // scene file (the ordinary case under version control) would
            // otherwise keep every reference on disk while the safeguard
            // ignores that scene, and the clones would be deleted. So a failed
            // save collects NOTHING.
            List<string> deleted = sceneGuid != null && saved
                ? StaticInstanceWindCloneRegistry.CollectForClearedScene(sceneGuid, scene.path)
                : new List<string>();

            // The unsaved-scene case is reported rather than silently doing half the job:
            // a scene with no path has no GUID, so there is no row to collect against.
            string cloneNote = sceneGuid == null
                ? " Wind-stripped clones were not collected: the scene has never been " +
                  "saved, so it has no identity to have recorded them under."
                : !saved
                    ? " Wind-stripped clones were NOT collected: saving the scene failed, " +
                      "so the copy on disk still references them and deleting any would " +
                      "break it. **The bake data is cleared in memory and nothing is " +
                      "persisted** — the usual cause is the scene file being read-only " +
                      "under version control, so check it out (or undo the clear), then " +
                      "press Clear again. `Undo` restores the cleared components."
                    : deleted.Count > 0
                        ? $" {deleted.Count} wind-stripped material clone(s) collected."
                        : " No wind-stripped clone became collectable.";

            // Said out loud because otherwise it looks like the GC is broken.
            // Clones the registry never recorded are deliberately untouchable
            // (for example after a lost or unreadable ledger),
            // and without this line the only visible evidence is a folder that
            // never shrinks. The advice names the remedy that is always safe:
            // a clone something still references is counted here too, so
            // deleting by hand could break that reference silently.
            int unknown = sceneGuid != null
                ? StaticInstanceWindCloneRegistry.UnknownClonesOnDisk().Count
                : 0;
            string unknownNote = unknown > 0
                ? $" {unknown} clone(s) in the folder are unrecorded and left alone by " +
                  "design; some may still be referenced. Press 'Bake Sources' in the " +
                  "scenes that use them to bring them under the registry, which is what " +
                  "makes them collectable later."
                : string.Empty;

            // Reported rather than silently ignored: a target in an additively loaded
            // scene was deliberately not touched, and the user is entitled to know the
            // press did less than the section's other buttons would have.
            string scopeNote = skippedOtherScenes > 0
                ? $" {skippedOtherScenes} target(s) in other loaded scenes were left " +
                  "untouched — this action is scoped to the active scene."
                : string.Empty;

            string report =
                $"Clear baked data: {cleared} target(s) cleared in '{scene.name}', " +
                (saved ? "scene saved." : "SCENE NOT SAVED.") +
                cloneNote + unknownNote + scopeNote;

            // A failed save is the one outcome where this action destroyed something and
            // persisted nothing, so it warns rather than logs: at
            // `Log` severity the single line that says the data is only in memory sits in
            // the console next to every ordinary message.
            if (saved)
                Debug.Log(report);
            else
                Debug.LogWarning(report);
        }

        // Manual diagnosis without baking; builds run the same validation and
        // fail fast on errors.
        public static void ValidateAllInScene() => ValidateAllInSceneWithResults();

        // Same pass and the same console output, plus the findings returned
        // so the Static Instances window can list the objects behind each one.
        public static List<ValidationResult> ValidateAllInSceneWithResults()
        {
            var results = new List<ValidationResult>();
            int errors = 0;
            foreach (MonoBehaviour group in FindBakeTargetsInScene())
            {
                List<Issue> issues = Validate(group);
                LogIssues(group, issues);
                errors += issues.FindAll(issue => issue.Severity == LogType.Error).Count;
                results.Add(new ValidationResult(group, issues));
            }

            Debug.Log($"Validation finished: {errors} error(s). See the console above, " +
                "or the Validation Results list in Window > Rendering > Static Instances.");
            return results;
        }

        private static void AppendPath(StringBuilder builder, Transform node, Transform root)
        {
            builder.Append(GetPath(node, root)).Append('|');
        }

        private static string GetPath(Transform node, Transform root)
        {
            var parts = new List<string>(8);
            for (Transform current = node; current != null && current != root; current = current.parent)
                parts.Add(current.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static void AppendMatrix(StringBuilder builder, Matrix4x4 matrix)
        {
            for (int i = 0; i < 16; i++)
            {
                builder.Append(matrix[i].ToString("R", CultureInfo.InvariantCulture));
                builder.Append(',');
            }

            builder.Append('|');
        }

        private static void AppendAssetId(StringBuilder builder, UnityEngine.Object asset)
        {
            if (asset != null &&
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string guid, out long localId))
            {
                builder.Append(guid).Append(':').Append(localId).Append('|');
            }
            else
            {
                builder.Append(asset != null ? asset.name : "null").Append('|');
            }
        }
    }
}
