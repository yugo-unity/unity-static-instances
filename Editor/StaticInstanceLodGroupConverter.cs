using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace StaticInstances.Editor
{
    // Dissolves the LOD Groups in the open scene, keeping each group's LOD0
    // renderers and removing the coarser ones, and converts the groups'
    // screen-relative transition heights into the metric ladder Static
    // Instances uses. The coarse levels are re-supplied by Mesh LODs,
    // so the visual ladder survives the removal — what goes away is the
    // per-object LODGroup component and the duplicated coarse geometry that
    // makes a scene expensive to cull and impossible to instance.
    //
    // Two properties this tool is built around, because both are easy to get
    // wrong and expensive to discover afterwards:
    //
    // - **Nothing is mutated before the user has seen the numbers.** Analysis
    //   and execution are separate passes over the same data, and the preview
    //   reports exactly what the execution will do. A conversion can touch
    //   thousands of objects; "run it and look" is not a review.
    // - **Scene changes undo in one step; asset changes cannot.** This class
    //   only performs scene surgery, which is why it can promise that. The
    //   importer-flag and copy-asset stages are deliberately not folded in
    //   here — they are not undoable, and mixing them into a collapsed undo
    //   group would make the promise a lie.
    internal static class StaticInstanceLodGroupConverter
    {
        // The conversion band's gate. Scope is the owner's own subtree, never the
        // scene: a scene may deliberately keep LOD Group content elsewhere, so a
        // scene-wide test would block baking on targets with nothing wrong with them.
        //
        // The bake-suppression half of the gate needs no code here — Validate
        // already emits an Error per LODGroup found in exactly this scope,
        // and both the build processor and the play-mode baker skip a target
        // that has validation errors. This helper exists for the inspector,
        // which has to decide what to draw.
        // Both answers the inspector band needs, from one walk: how many LOD
        // Groups block this owner's bake, and how many of them the converter
        // would actually take. The band has to tell those apart — where every
        // group is refused as dynamic, "convert to fix this" sends the user in
        // a circle while the bake stays blocked.
        //
        // Deliberately not a full Analyze: no ladder, no doomed list. It is
        // still a hierarchy walk, so callers cache it per selection.
        internal static void InspectSubtree(
            Component owner, out int lodGroups, out int convertible)
        {
            lodGroups = 0;
            convertible = 0;
            if (owner == null)
                return;

            // Ownership-filtered: a nested owner's LOD Groups block the
            // nested owner's bake, and its own inspector says so. Counting
            // them here would put the warning on a component that cannot act
            // on it, and its Convert would reach across the boundary.
            foreach (LODGroup group in StaticInstanceOwnership.OwnedComponents<LODGroup>(owner))
            {
                if (group == null)
                    continue;

                lodGroups++;
                if (FindDynamicEvidence(group.transform) == null)
                    convertible++;
            }
        }

        // Converts one owner's subtree, for the inspector's local Convert.
        internal static Plan AnalyzeSubtree(Component owner, float fieldOfViewDegrees)
        {
            return Analyze(new[] { owner.gameObject }, fieldOfViewDegrees);
        }

        // Above this, Undo recording itself becomes the dominant cost and can
        // exhaust the undo buffer anyway, so the user is offered the choice
        // rather than left waiting.
        private const int UndoRecordingThreshold = 2000;

        // One SI owner's own split. `Qualifying` counts its groups whose
        // LOD ≥ 1 renderers are ALL ShadowCastingMode.Off; `Candidates` counts
        // those that have LOD ≥ 1 renderers at all — a single-level group is
        // neither, because there is no coarse intent to destroy.
        internal readonly struct ShadowIntentOwner
        {
            public readonly Component Owner;
            public readonly int Qualifying;
            public readonly int Candidates;

            public ShadowIntentOwner(Component owner, int qualifying, int candidates)
            {
                Owner = owner;
                Qualifying = qualifying;
                Candidates = candidates;
            }

            // Strict majority of THIS owner's own candidate groups.
            public bool Derives => Candidates > 0 && Qualifying * 2 > Candidates;

            // A derived threshold is an index into a ladder, so on an owner whose ladder
            // is not live it names the only level that owner ever draws and casting never
            // stops: the write happens and changes nothing (e.g. an owner with
            // `LodEnabled` off).
            //
            // This mirrors the runtime's own `active` expression in
            // `StaticInstanceScatter.ApplyLodParams` (cited by name, not line),
            // including its mode-awareness. It is the same predicate stated twice, not a
            // second policy: keep the two in sync.
            public bool HasLiveLadder
            {
                get
                {
                    if (Owner is StaticInstanceScatter scatter)
                    {
                        return LadderLive(scatter.LodEnabled, scatter.MinMeshLodLevel,
                            scatter.MaxMeshLodLevel, scatter.LodMode,
                            scatter.LodDistances, scatter.LodRatios);
                    }
                    return false;
                }
            }

            private static bool LadderLive(
                bool lodEnabled, int minLevel, int maxLevel,
                StaticInstanceLodMode mode, float[] distances, float[] ratios)
            {
                int boundaryCount = maxLevel - minLevel;
                return lodEnabled && boundaryCount > 0 &&
                    StaticInstanceScatter.HasLadderFor(
                        boundaryCount, mode, distances, ratios);
            }
        }

        internal sealed class Plan
        {
            public readonly List<LODGroup> Groups = new List<LODGroup>();
            public int KeptRenderers;
            public int RemovedRenderers;
            // Median boundary distance per ladder level, metres. Median rather
            // than mean: one oversized group (a terrain-scale shell dropped
            // into the same scene) would drag a mean far past every real prop.
            public float[] LadderDistances = new float[0];
            // Which owners author the "LOD1+ do not cast"
            // intent the conversion would otherwise destroy, and how strongly.
            // The SPLIT is surfaced rather than reduced to a verdict —
            // deriving on a majority means the minority loses its shadows,
            // which is visible, so the preview names the counts.
            //
            // Per owner, because the majority policy is per owner: a plan-wide majority
            // would write N = 0 to an owner that authors the intent nowhere, carried
            // over the line by another owner's groups.
            public readonly List<ShadowIntentOwner> ShadowIntent =
                new List<ShadowIntentOwner>();
            public bool DeriveShadowCastMaxLevel;
            public bool LodBiasFolded;
            public float LodBias = 1f;
            public int GroupsWithoutRenderers;
            // Groups left alone because their subtree shows evidence of being
            // dynamic, with a per-reason tally for the preview. Carried per
            // group (not just as a tally) so the preview can offer the
            // per-group "convert anyway" opt-in: the detection is evidence,
            // not proof, and a user who knows a given group is static needs a
            // way through that does not mean forcing all of them.
            public readonly List<SkippedGroup> Skipped = new List<SkippedGroup>();
            public readonly Dictionary<string, int> SkipReasons = new Dictionary<string, int>();
            // Groups that carry dynamic evidence and are being converted
            // anyway on the user's explicit per-group say-so.
            public readonly List<LODGroup> Forced = new List<LODGroup>();
            // The roots this plan was analysed from, so a changed opt-in can
            // be re-analysed rather than patched. Keeping analysis the only
            // producer of a Plan is what stops the preview from lying about
            // what Execute will do.
            public GameObject[] Roots = new GameObject[0];
            // Non-Unity components that sit on GameObjects this conversion
            // would DESTROY. Losing a user's own script silently is the worst
            // thing this tool could do, so they are counted by type and put
            // in front of the user before anything happens.
            public readonly Dictionary<string, int> DoomedCustomComponents =
                new Dictionary<string, int>();

            public bool HasDoomedCustomComponents => DoomedCustomComponents.Count > 0;

            // Every GameObject the conversion would destroy, so the preview
            // can offer protection for ANY of them — not only the ones
            // carrying a custom component. A custom component that drives a
            // *different* object is undetectable from here, and that other
            // object is an ordinary coarse-LOD target, so the user has to be
            // able to reach it and mark it by hand.
            public readonly List<DoomedObject> Doomed = new List<DoomedObject>();

            public int GroupCount => Groups.Count;
            public bool IsEmpty => Groups.Count == 0;
        }

        internal readonly struct SkippedGroup
        {
            public readonly LODGroup Group;
            // The component type that made this look dynamic.
            public readonly string Reason;

            public SkippedGroup(LODGroup group, string reason)
            {
                Group = group;
                Reason = reason;
            }
        }

        internal readonly struct DoomedObject
        {
            public readonly GameObject Object;
            // Comma-separated custom component type names, empty when none.
            public readonly string CustomComponents;

            public DoomedObject(GameObject target, string customComponents)
            {
                Object = target;
                CustomComponents = customComponents;
            }

            public bool HasCustomComponents => !string.IsNullOrEmpty(CustomComponents);
        }

        // Pure analysis: walks the scene and computes what a conversion would
        // do, without touching anything. The preview and the execution both
        // consume this, so what the dialog reports is what runs.
        // `convertAnyway` holds groups the user explicitly opted in from the
        // preview despite their dynamic evidence. Re-analysing with the set
        // (rather than patching an existing Plan) keeps the counts, the doomed
        // list and the ladder consistent with what Execute will do.
        internal static Plan Analyze(
            IEnumerable<GameObject> roots,
            float fieldOfViewDegrees,
            HashSet<LODGroup> convertAnyway = null)
        {
            var plan = new Plan();
            var rootList = new List<GameObject>();
            foreach (GameObject root in roots)
            {
                if (root != null)
                    rootList.Add(root);
            }

            plan.Roots = rootList.ToArray();
            roots = plan.Roots;
            // LODGroup multiplies QualitySettings.lodBias at runtime, so the
            // distance the author actually saw includes it. Folding it in is
            // what keeps a lodBias != 1 project's converted scene looking the
            // same; the converted result is metric and stops tracking future
            // lodBias changes, which is deliberate.
            plan.LodBias = Mathf.Max(0.0001f, QualitySettings.lodBias);
            plan.LodBiasFolded = !Mathf.Approximately(plan.LodBias, 1f);

            float halfAngleTangent = Mathf.Tan(
                Mathf.Deg2Rad * Mathf.Clamp(fieldOfViewDegrees, 1f, 179f) * 0.5f);
            var perBoundary = new List<List<float>>();

            foreach (GameObject root in roots)
            {
                if (root == null)
                    continue;

                foreach (LODGroup group in root.GetComponentsInChildren<LODGroup>(true))
                {
                    if (group == null)
                        continue;

                    string dynamicReason = FindDynamicEvidence(group.transform);
                    if (dynamicReason != null)
                    {
                        if (convertAnyway == null || !convertAnyway.Contains(group))
                        {
                            plan.Skipped.Add(new SkippedGroup(group, dynamicReason));
                            plan.SkipReasons.TryGetValue(dynamicReason, out int count);
                            plan.SkipReasons[dynamicReason] = count + 1;
                            continue;
                        }

                        plan.Forced.Add(group);
                    }

                    plan.Groups.Add(group);
                    LOD[] lods = group.GetLODs();
                    if (lods.Length == 0)
                    {
                        plan.GroupsWithoutRenderers++;
                        continue;
                    }

                    var keep = new HashSet<Renderer>();
                    foreach (Renderer renderer in lods[0].renderers)
                    {
                        if (renderer != null && keep.Add(renderer))
                            plan.KeptRenderers++;
                    }
                    HashSet<Transform> keptAncestors = BuildKeptAncestorIndex(keep);

                    for (int level = 1; level < lods.Length; level++)
                    {
                        foreach (Renderer renderer in lods[level].renderers)
                        {
                            if (renderer == null || keep.Contains(renderer))
                                continue;

                            plan.RemovedRenderers++;
                            // Mirrors Execute exactly: the GameObject only
                            // dies when no kept renderer lives under it.
                            // Otherwise just the MeshFilter/Renderer go and
                            // anything else on the object survives.
                            if (!keptAncestors.Contains(renderer.transform))
                            {
                                plan.Doomed.Add(new DoomedObject(
                                    renderer.gameObject,
                                    CollectCustomComponents(renderer.transform, plan)));
                            }
                        }
                    }

                    // The group's world size, the same quantity LODGroup
                    // itself scales by. Deliberately NOT size culling's "instance
                    // world size" (a bounding-sphere diameter): this one has
                    // to match LOD Group semantics, because it is converting
                    // LOD Group-authored data. The two definitions are
                    // documented side by side and never unified.
                    Vector3 scale = group.transform.lossyScale;
                    float worldSize = group.size *
                        Mathf.Max(Mathf.Abs(scale.x), Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z)));

                    // Boundary i is the transition out of level i, so a group
                    // with N levels contributes N-1 boundaries. The last
                    // level's height is the cull point, not a transition.
                    for (int level = 0; level < lods.Length - 1; level++)
                    {
                        float height = lods[level].screenRelativeTransitionHeight;
                        if (height <= 0f || worldSize <= 0f)
                            continue;

                        while (perBoundary.Count <= level)
                            perBoundary.Add(new List<float>());

                        // Relative height, so no reference resolution is
                        // needed — the vertical FOV alone sets the scale.
                        perBoundary[level].Add(
                            plan.LodBias * worldSize / (height * 2f * halfAngleTangent));
                    }
                }
            }

            plan.LadderDistances = new float[perBoundary.Count];
            for (int i = 0; i < perBoundary.Count; i++)
            {
                List<float> values = perBoundary[i];
                values.Sort();
                plan.LadderDistances[i] = values.Count == 0
                    ? 0f
                    : values[values.Count / 2];
            }

            CollectShadowIntent(plan);
            return plan;
        }

        // Scene surgery only. Returns the number of LOD Groups dissolved.
        // `protectedObjects` are GameObjects the user marked in the preview as
        // must-not-be-destroyed. They keep their GameObject and everything on
        // it; only the coarse MeshFilter/Renderer are removed, so the
        // conversion still means something while whatever else lives there
        // survives. That distinction matters most for the case nothing can
        // detect: a custom component on one object driving a *different* one.
        // The driven object looks like an ordinary coarse-LOD target from
        // here, so marking it by hand is the only way to save it.
        internal static int Execute(
            Plan plan, bool recordUndo, HashSet<GameObject> protectedObjects = null)
        {
            int dissolved = 0;
            int undoGroup = Undo.GetCurrentGroup();

            foreach (LODGroup group in plan.Groups)
            {
                if (group == null)
                    continue;

                // Unpacked scene content is expected; a stray prefab instance
                // is unpacked completely so the surgery below is legal at all
                // (a prefab instance refuses component removal otherwise).
                if (PrefabUtility.IsPartOfPrefabInstance(group))
                {
                    GameObject outermost = PrefabUtility.GetOutermostPrefabInstanceRoot(group);
                    if (outermost != null)
                    {
                        PrefabUtility.UnpackPrefabInstance(
                            outermost, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                    }
                }

                LOD[] lods = group.GetLODs();
                var keep = new HashSet<Renderer>();
                if (lods.Length > 0)
                {
                    foreach (Renderer renderer in lods[0].renderers)
                    {
                        if (renderer != null)
                            keep.Add(renderer);
                    }
                }
                HashSet<Transform> keptAncestors = BuildKeptAncestorIndex(keep);

                for (int level = 1; level < lods.Length; level++)
                {
                    foreach (Renderer renderer in lods[level].renderers)
                    {
                        if (renderer == null || keep.Contains(renderer))
                            continue;

                        // A coarse renderer's GameObject is destroyed only
                        // when no kept renderer lives under it; otherwise the
                        // renderer components alone go, so the transform that
                        // parents kept geometry survives.
                        bool isProtected = protectedObjects != null &&
                            protectedObjects.Contains(renderer.gameObject);
                        if (isProtected || keptAncestors.Contains(renderer.transform))
                        {
                            if (renderer.TryGetComponent(out MeshFilter filter))
                                Destroy(filter, recordUndo);
                            Destroy(renderer, recordUndo);
                        }
                        else
                        {
                            Destroy(renderer.gameObject, recordUndo);
                        }
                    }
                }

                Destroy(group, recordUndo);
                dissolved++;
            }

            if (recordUndo)
            {
                Undo.SetCurrentGroupName("Convert LOD Groups");
                Undo.CollapseUndoOperations(undoGroup);
            }

            return dissolved;
        }

        private static void Destroy(Object target, bool recordUndo)
        {
            if (recordUndo)
                Undo.DestroyObjectImmediate(target);
            else
                Object.DestroyImmediate(target);
        }

        // What this converter is for is letting a project take Static
        // Instances' benefit cheaply — not trading behaviour risk for speed.
        // So a group whose subtree shows evidence of being dynamic is left
        // entirely alone rather than converted and hoped about. Differences
        // in LOD behaviour are accepted (that is the feature); an object that
        // turns out to move, animate or simulate is not.
        //
        // Evidence, not proof: a script that moves a transform every frame is
        // undetectable from here, and nothing can find it. What is detectable
        // is the standard machinery such content carries. Deliberately NOT
        // keyed on GameObject.isStatic: real scenes often leave it unset on content
        // that never moves, so that test would exclude nearly everything.
        // Anything the user (or a third-party package) authored, as opposed to
        // a component Unity ships. The test is the defining assembly: Unity's
        // own live in UnityEngine.* / UnityEditor.* / Unity.*, so everything
        // else came from the user's project or an installed package. A heuristic,
        // but one that errs toward *asking* — a false positive costs a dialog
        // line, a false negative costs someone's script.
        private static string CollectCustomComponents(Transform doomed, Plan plan)
        {
            List<string> names = null;
            foreach (Component component in doomed.GetComponentsInChildren<Component>(true))
            {
                if (component == null)
                    continue;

                System.Type type = component.GetType();
                string assembly = type.Assembly.GetName().Name;
                if (assembly.StartsWith("UnityEngine", System.StringComparison.Ordinal) ||
                    assembly.StartsWith("UnityEditor", System.StringComparison.Ordinal) ||
                    assembly.StartsWith("Unity.", System.StringComparison.Ordinal))
                {
                    continue;
                }

                names ??= new List<string>();
                if (!names.Contains(type.Name))
                    names.Add(type.Name);
                plan.DoomedCustomComponents.TryGetValue(type.Name, out int count);
                plan.DoomedCustomComponents[type.Name] = count + 1;
            }

            return names == null ? string.Empty : string.Join(", ", names);
        }

        private static string FindDynamicEvidence(Transform root)
        {
            if (root.GetComponentInChildren<Animator>(true) != null)
                return "Animator";
            if (root.GetComponentInChildren<Animation>(true) != null)
                return "Animation";
            if (root.GetComponentInChildren<Rigidbody>(true) != null)
                return "Rigidbody";
            if (root.GetComponentInChildren<Rigidbody2D>(true) != null)
                return "Rigidbody2D";
            if (root.GetComponentInChildren<ParticleSystem>(true) != null)
                return "ParticleSystem";
            if (root.GetComponentInChildren<Cloth>(true) != null)
                return "Cloth";
            // Static Instances draws MeshRenderers; a skinned renderer is
            // both deforming and outside what the bake can take.
            if (root.GetComponentInChildren<SkinnedMeshRenderer>(true) != null)
                return "SkinnedMeshRenderer";
            return null;
        }

        // A coarse renderer's object must survive when its subtree contains any
        // LOD0 renderer. Indexing every kept renderer's full ancestor chain makes
        // that query constant-time and preserves references outside the LODGroup
        // hierarchy: no artificial stop is made at the group root.
        internal static HashSet<Transform> BuildKeptAncestorIndex(HashSet<Renderer> keep)
        {
            var ancestors = new HashSet<Transform>();
            if (keep == null)
                return ancestors;

            foreach (Renderer renderer in keep)
            {
                for (Transform current = renderer != null ? renderer.transform : null;
                     current != null && ancestors.Add(current);
                     current = current.parent)
                {
                }
            }
            return ancestors;
        }

        // Threshold check kept next to the constant it guards, so the caller
        // does not have to know the number.
        internal static bool ExceedsUndoThreshold(Plan plan)
        {
            return plan.KeptRenderers + plan.RemovedRenderers > UndoRecordingThreshold;
        }

        // Classify each SI owner under the roots by ITS OWN
        // groups. Ownership-filtered, so a nested owner's groups are counted
        // against the nested owner and never against its parent — the same rule
        // InspectSubtree follows, and the reason `OwnedComponents` exists.
        private static void CollectShadowIntent(Plan plan)
        {
            // Only groups this conversion will actually dissolve. `plan.Groups` excludes
            // those dropped for dynamic evidence; they keep their LOD Group and its
            // casting intent, so counting them would let surviving intent decide a
            // write about destroyed intent.
            var converted = new HashSet<LODGroup>(plan.Groups);
            var seen = new HashSet<Component>();
            foreach (GameObject root in plan.Roots)
            {
                if (root == null)
                    continue;

                foreach (Component owner in root.GetComponentsInChildren<Component>(true))
                {
                    if (!(owner is StaticInstanceScatter) || !seen.Add(owner))
                    {
                        continue;
                    }

                    int qualifying = 0;
                    int candidates = 0;
                    foreach (LODGroup group in
                        StaticInstanceOwnership.OwnedComponents<LODGroup>(owner))
                    {
                        if (group == null || !converted.Contains(group))
                            continue;

                        LOD[] lods = group.GetLODs();
                        if (lods == null || lods.Length <= 1)
                            continue;

                        bool anyCoarse = false;
                        bool allCoarseSilent = true;
                        for (int level = 1; level < lods.Length; level++)
                        {
                            foreach (Renderer renderer in lods[level].renderers)
                            {
                                if (renderer == null)
                                    continue;
                                anyCoarse = true;
                                if (renderer.shadowCastingMode !=
                                    UnityEngine.Rendering.ShadowCastingMode.Off)
                                {
                                    allCoarseSilent = false;
                                }
                            }
                        }

                        if (!anyCoarse)
                            continue;

                        candidates++;
                        if (allCoarseSilent)
                            qualifying++;
                    }

                    if (candidates > 0)
                        plan.ShadowIntent.Add(new ShadowIntentOwner(owner, qualifying, candidates));
                }
            }
        }

        // Writes the derived Shadow Cast Max Level. N = 0 — casting stops at the
        // boundary that ends level 0 — because "LOD1+ do not cast" is exactly
        // that statement in ladder vocabulary.
        //
        // Opt-in and majority-based, per owner: an owner whose groups do not reach
        // the majority is left alone, so an owner that authors the intent nowhere
        // is never written on the strength of another owner's content.
        internal static int ApplyShadowCastDerivation(Plan plan, bool recordUndo)
        {
            if (plan == null || !plan.DeriveShadowCastMaxLevel)
                return 0;

            int written = 0;
            foreach (ShadowIntentOwner row in plan.ShadowIntent)
            {
                if (!row.Derives)
                    continue;

                // The target is the owner's finest level, not the literal 0: "LOD1+ do not
                // cast" means "only the finest level casts", and on an owner whose window
                // starts above 0 that level is `MinMeshLodLevel`. The setter floors at the
                // window minimum, so writing 0 would never equal the guard's 0 and the
                // write would repeat on every run.
                if (row.Owner is StaticInstanceScatter scatter)
                {
                    int target = Mathf.Max(0, scatter.MinMeshLodLevel);
                    if (scatter.ShadowCastMaxLevel == target)
                        continue;
                    if (recordUndo)
                        Undo.RecordObject(scatter, "Derive Shadow Cast Max Level");
                    scatter.ShadowCastMaxLevel = target;
                    EditorUtility.SetDirty(scatter);
                    written++;
                }
            }

            return written;
        }
    }
}
