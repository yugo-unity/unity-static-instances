using System.Collections.Generic;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace StaticInstances.Editor
{
    // Pre-execution preview for Convert LOD Groups. A window rather than a
    // dialog because a conversion can destroy thousands of GameObjects, and the
    // user has to be able to find specific ones and protect them.
    //
    // Nothing here mutates the scene. The window holds a Plan produced by a
    // pure analysis pass and hands it back to Execute only when Convert is
    // pressed, so closing the window is always a complete no-op.
    internal sealed class StaticInstanceConvertPreviewWindow : EditorWindow
    {
        private StaticInstanceLodGroupConverter.Plan plan;
        private float fieldOfView;
        private string scopeName;

        private readonly HashSet<GameObject> protectedObjects = new HashSet<GameObject>();
        // Every doomed object the window has already applied its default
        // protection to. Re-analysis (after a "convert anyway" opt-in) brings
        // new objects into the list; without this, re-seeding would also undo
        // the user's own toggles on the objects that were already there.
        private readonly HashSet<GameObject> seededDoomed = new HashSet<GameObject>();
        // Groups the user opted in despite their dynamic evidence.
        private readonly HashSet<LODGroup> convertAnyway = new HashSet<LODGroup>();
        // A window field, like protectedObjects and convertAnyway: Reanalyze
        // replaces the Plan wholesale, so a choice stored on the Plan would be
        // silently discarded.
        private bool deriveShadowCastMaxLevel;
        private readonly List<int> visibleRows = new List<int>();
        private bool customOnly = true;
        private string search = string.Empty;
        // Rebuilt lazily: a SearchField does not survive a domain reload, and
        // the window can outlive one.
        private SearchField searchField;
        private Vector2 scroll;
        private Vector2 skippedScroll;
        private bool filterDirty = true;

        private const float RowHeight = 20f;
        // Rows of the skipped-groups list shown before it scrolls. Small on
        // purpose: it is a secondary list, and dynamic groups under a Static
        // Instance component are the exception, not the bulk.
        private const int SkippedVisibleRows = 6;

        internal static void Open(
            StaticInstanceLodGroupConverter.Plan plan, float fieldOfView, string scopeName)
        {
            var window = GetWindow<StaticInstanceConvertPreviewWindow>(true, "Convert LOD Groups", true);
            window.plan = plan;
            window.fieldOfView = fieldOfView;
            window.scopeName = scopeName;
            window.protectedObjects.Clear();
            window.seededDoomed.Clear();
            window.convertAnyway.Clear();
            window.SeedProtection();

            window.filterDirty = true;
            window.minSize = new Vector2(520f, 420f);
            window.Show();
        }

        // Default: protect everything carrying a custom (non-engine) component,
        // so the safe state is the starting state and deleting is the deliberate act.
        // Applied once per object, so a later re-analysis leaves earlier choices be.
        private void SeedProtection()
        {
            foreach (StaticInstanceLodGroupConverter.DoomedObject doomed in plan.Doomed)
            {
                if (doomed.Object == null || !seededDoomed.Add(doomed.Object))
                    continue;
                if (doomed.HasCustomComponents)
                    protectedObjects.Add(doomed.Object);
            }
        }

        // An opt-in changes the counts, the doomed list and the ladder, so the
        // plan is rebuilt by the same analysis that produced it rather than
        // patched. That is what keeps "the preview cannot lie" true: there is
        // still exactly one producer of a Plan, and Execute consumes it.
        private void Reanalyze()
        {
            plan = StaticInstanceLodGroupConverter.Analyze(plan.Roots, fieldOfView, convertAnyway);
            SeedProtection();
            filterDirty = true;
        }

        private void OnGUI()
        {
            if (plan == null)
            {
                EditorGUILayout.HelpBox("No conversion plan — reopen from Graphics Settings.", MessageType.Info);
                return;
            }

            DrawHeader();
            EditorGUILayout.Space();
            DrawSkipped();
            EditorGUILayout.Space();
            DrawFilters();
            DrawList();
            DrawFooter();
        }

        private void DrawHeader()
        {
            EditorGUILayout.LabelField($"Scope: {scopeName}", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                $"Dissolve {plan.GroupCount:N0} LOD Group(s) · keep {plan.KeptRenderers:N0} LOD0 " +
                $"renderer(s) · remove {plan.RemovedRenderers:N0} coarser");

            if (plan.LadderDistances.Length > 0)
            {
                var parts = new List<string>(plan.LadderDistances.Length);
                foreach (float d in plan.LadderDistances)
                    parts.Add($"{d:F1} m");
                EditorGUILayout.LabelField(
                    $"Ladder at {fieldOfView:F0}° FOV (median): {string.Join(" / ", parts)}");
            }

            // The decision is per owner, so the preview reports per owner: an aggregate
            // would let one owner's content authorize a write to another's. The splits
            // are shown rather than a verdict, because deriving on a majority stops the
            // minority casting shadows, and the number is what makes the opt-in informed.
            if (plan.ShadowIntent.Count > 0)
            {
                int deriving = 0;
                int partial = 0;
                int inert = 0;
                var inertNames = new List<string>();
                foreach (var row in plan.ShadowIntent)
                {
                    if (!row.Derives)
                        continue;
                    deriving++;
                    if (row.Qualifying < row.Candidates)
                        partial++;
                    // A derived threshold on a ladder-less owner is written and does nothing.
                    if (!row.HasLiveLadder)
                    {
                        inert++;
                        if (row.Owner != null)
                            inertNames.Add(row.Owner.name);
                    }
                }

                using (new EditorGUI.DisabledScope(deriving == 0))
                {
                    deriveShadowCastMaxLevel = EditorGUILayout.ToggleLeft(
                        new GUIContent(
                            "Restore \"LOD1+ do not cast\" as Shadow Cast Max Level " +
                            $"({deriving:N0} of {plan.ShadowIntent.Count:N0} owner(s))",
                            "Restore a shadow-level cutoff from each owner's own group majority. " +
                            "Review partial-majority and missing-ladder warnings below."),
                        deriving > 0 && deriveShadowCastMaxLevel);
                }

                if (deriving == 0)
                {
                    EditorGUILayout.HelpBox(
                        "No owner here has a majority of groups authoring \"LOD1+ do not " +
                        "cast\", so nothing would be derived. Set Shadow Cast Max Level " +
                        "by hand where it belongs.",
                        MessageType.None);
                }
                else if (partial > 0)
                {
                    var worst = new List<string>();
                    foreach (var row in plan.ShadowIntent)
                    {
                        if (row.Derives && row.Qualifying < row.Candidates)
                        {
                            worst.Add(
                                $"{row.Owner.name} {row.Qualifying:N0}/{row.Candidates:N0}");
                        }
                    }

                    worst.Sort();
                    EditorGUILayout.HelpBox(
                        $"{partial:N0} of the {deriving:N0} owner(s) would be derived on a " +
                        "PARTIAL majority — their remaining groups will stop casting " +
                        "shadows past the derived level, which is visible:\n" +
                        string.Join(", ", worst.GetRange(0, Mathf.Min(8, worst.Count))) +
                        (worst.Count > 8 ? $", +{worst.Count - 8:N0} more" : string.Empty),
                        MessageType.Warning);
                }

                // The threshold is an index into a ladder, so on an owner whose ladder is
                // not live it names the only level that owner ever draws: the value is
                // written and casting never stops. Say so rather than report "n owners
                // derived" while some of them cannot act on it.
                if (inert > 0)
                {
                    inertNames.Sort();
                    bool all = inert == deriving;
                    EditorGUILayout.HelpBox(
                        (all
                            ? $"NONE of the {deriving:N0} owner(s) has a live LOD ladder, so "
                            : $"{inert:N0} of the {deriving:N0} owner(s) has no live LOD ladder, so ") +
                        "the derived threshold would be written and have NO EFFECT there — " +
                        "a level index needs levels to index, and these owners never leave " +
                        "their finest one. Give them a ladder, or set the shadow cut by " +
                        "other means:\n" +
                        string.Join(", ", inertNames.GetRange(0, Mathf.Min(8, inertNames.Count))) +
                        (inertNames.Count > 8 ? $", +{inertNames.Count - 8:N0} more" : string.Empty),
                        all ? MessageType.Warning : MessageType.Info);
                }
            }

            if (plan.LodBiasFolded)
            {
                EditorGUILayout.HelpBox(
                    $"QualitySettings.lodBias is {plan.LodBias:F2} and has been folded in, so the " +
                    "converted distances match the current appearance. The result is metric and will " +
                    "NOT track future lodBias changes.",
                    MessageType.Info);
            }

            // Nothing here will convert, so "convert to fix it" would be a dead end
            // while the bake stays blocked.
            if (plan.GroupCount == 0 && plan.Skipped.Count > 0)
            {
                EditorGUILayout.HelpBox(
                    $"Nothing will convert: all {plan.Skipped.Count:N0} LOD Group(s) here look " +
                    $"dynamic ({Describe(plan.SkipReasons)}).\n\n" +
                    "While they stay under a Static Instance component its bake cannot run — " +
                    "move those objects out of the subtree, remove the LOD Group by hand, or, " +
                    "if the detection is wrong, tick the ones you know are static below.",
                    MessageType.Warning);
            }
            else if (plan.Skipped.Count > 0)
            {
                EditorGUILayout.HelpBox(
                    $"{plan.Skipped.Count:N0} LOD Group(s) left alone — their subtrees look dynamic " +
                    $"({Describe(plan.SkipReasons)}). Converting those would risk changing behaviour; " +
                    "tick any you know are static below.",
                    MessageType.Info);
            }

            if (plan.Forced.Count > 0)
            {
                EditorGUILayout.HelpBox(
                    $"{plan.Forced.Count:N0} LOD Group(s) will be converted despite their dynamic " +
                    "evidence, on your say-so. If any of them really does move, animate or " +
                    "simulate, the conversion changes its behaviour.",
                    MessageType.Warning);
            }

            EditorGUILayout.HelpBox(
                "Scene changes undo in one step. Importer flags and generated mesh assets are " +
                "separate stages and are NOT undoable.\n\n" +
                "A custom component that drives a DIFFERENT GameObject cannot be detected here — " +
                "if you have one, find that object below and protect it by hand.",
                MessageType.Warning);
        }

        // Per-group "convert anyway": the dynamic detection is evidence, not proof
        // (an Animator anywhere in the subtree skips the whole group), so the user
        // needs a way through. Per group, never a blanket force flag, so consent is
        // given about a named object.
        private void DrawSkipped()
        {
            int total = plan.Skipped.Count + convertAnyway.Count;
            if (total == 0)
                return;

            EditorGUILayout.LabelField(
                $"Skipped as dynamic — {plan.Skipped.Count:N0} left alone, " +
                $"{convertAnyway.Count:N0} opted in",
                EditorStyles.miniLabel);

            // Both lists are drawn from the same source so a toggled row does
            // not jump out of view: the plan's remaining skips plus the ones
            // the opt-in already moved into Groups.
            var rows = new List<(LODGroup Group, string Reason, bool OptedIn)>(total);
            foreach (StaticInstanceLodGroupConverter.SkippedGroup skipped in plan.Skipped)
                rows.Add((skipped.Group, skipped.Reason, false));
            foreach (LODGroup forced in plan.Forced)
                rows.Add((forced, "opted in", true));
            rows.Sort((a, b) => string.CompareOrdinal(
                a.Group != null ? a.Group.name : string.Empty,
                b.Group != null ? b.Group.name : string.Empty));

            float height = Mathf.Min(rows.Count, SkippedVisibleRows) * RowHeight + 4f;
            Rect viewport = GUILayoutUtility.GetRect(
                0f, height, GUILayout.ExpandWidth(true));
            var content = new Rect(0f, 0f, viewport.width - 20f, rows.Count * RowHeight);
            skippedScroll = GUI.BeginScrollView(viewport, skippedScroll, content);

            int first = Mathf.Max(0, Mathf.FloorToInt(skippedScroll.y / RowHeight) - 1);
            int last = Mathf.Min(
                rows.Count, first + Mathf.CeilToInt(viewport.height / RowHeight) + 2);

            LODGroup toggled = null;
            bool toggledTo = false;
            for (int row = first; row < last; row++)
            {
                (LODGroup group, string reason, bool optedIn) = rows[row];
                if (group == null)
                    continue;

                var rect = new Rect(0f, row * RowHeight, content.width, RowHeight);

                var toggleRect = new Rect(rect.x + 2f, rect.y + 2f, 18f, 16f);
                if (GUI.Toggle(toggleRect, optedIn, GUIContent.none) != optedIn)
                {
                    toggled = group;
                    toggledTo = !optedIn;
                }

                var labelRect = new Rect(rect.x + 22f, rect.y, rect.width - 90f, RowHeight);
                EditorGUI.LabelField(labelRect, $"{group.name}   —   {reason}");

                var pingRect = new Rect(rect.xMax - 64f, rect.y + 1f, 60f, RowHeight - 2f);
                if (GUI.Button(pingRect, "Select", EditorStyles.miniButton))
                {
                    Selection.activeGameObject = group.gameObject;
                    EditorGUIUtility.PingObject(group.gameObject);
                }
            }

            GUI.EndScrollView();

            // Applied after the loop: re-analysis rebuilds the very list being
            // enumerated.
            if (toggled != null)
            {
                if (toggledTo)
                    convertAnyway.Add(toggled);
                else
                    convertAnyway.Remove(toggled);
                Reanalyze();
            }
        }

        private void DrawFilters()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                customOnly = EditorGUILayout.ToggleLeft(
                    new GUIContent(
                        "Only objects with custom components",
                        "Turn off to list every GameObject scheduled for deletion. Protect externally driven objects manually."),
                    customOnly, GUILayout.Width(280f));

                // Unity's search control carries its meaning in the magnifier and clear
                // button, so it needs no label; the row it shares is already wide.
                searchField ??= new SearchField();
                search = searchField.OnToolbarGUI(search);
                if (EditorGUI.EndChangeCheck())
                    filterDirty = true;
            }
        }

        private void RebuildFilter()
        {
            visibleRows.Clear();
            for (int i = 0; i < plan.Doomed.Count; i++)
            {
                StaticInstanceLodGroupConverter.DoomedObject doomed = plan.Doomed[i];
                if (doomed.Object == null)
                    continue;
                if (customOnly && !doomed.HasCustomComponents)
                    continue;
                if (!string.IsNullOrEmpty(search) &&
                    doomed.Object.name.IndexOf(search, System.StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                visibleRows.Add(i);
            }

            filterDirty = false;
        }

        // Only the rows on screen are drawn: the unfiltered list can run to many
        // thousands of entries, and IMGUI does not survive drawing that many
        // controls per repaint.
        private void DrawList()
        {
            if (filterDirty)
                RebuildFilter();

            // Says which filter is hiding things, so "N shown of M" is never mistaken
            // for "only N will be deleted".
            bool searching = !string.IsNullOrEmpty(search);
            string narrowedBy = customOnly && searching
                ? " — custom components only, filtered by name"
                : customOnly
                    ? " — custom components only"
                    : searching
                        ? " — filtered by name"
                        : string.Empty;
            EditorGUILayout.LabelField(
                $"{visibleRows.Count:N0} shown of {plan.Doomed.Count:N0} object(s) to delete{narrowedBy}",
                EditorStyles.miniLabel);

            // Minimum height is a few rows, not position.height: asking for the whole
            // window would push the footer (Cancel / Convert) off-screen.
            // ExpandHeight still takes all the remaining room.
            Rect viewport = GUILayoutUtility.GetRect(
                0f, RowHeight * 4f, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            var content = new Rect(0f, 0f, viewport.width - 20f, visibleRows.Count * RowHeight);
            scroll = GUI.BeginScrollView(viewport, scroll, content);

            int first = Mathf.Max(0, Mathf.FloorToInt(scroll.y / RowHeight) - 1);
            int last = Mathf.Min(
                visibleRows.Count, first + Mathf.CeilToInt(viewport.height / RowHeight) + 2);

            for (int row = first; row < last; row++)
            {
                StaticInstanceLodGroupConverter.DoomedObject doomed = plan.Doomed[visibleRows[row]];
                if (doomed.Object == null)
                    continue;

                var rect = new Rect(0f, row * RowHeight, content.width, RowHeight);
                bool isProtected = protectedObjects.Contains(doomed.Object);

                var toggleRect = new Rect(rect.x + 2f, rect.y + 2f, 18f, 16f);
                bool next = GUI.Toggle(toggleRect, isProtected, GUIContent.none);
                if (next != isProtected)
                {
                    if (next)
                        protectedObjects.Add(doomed.Object);
                    else
                        protectedObjects.Remove(doomed.Object);
                }

                var labelRect = new Rect(rect.x + 22f, rect.y, rect.width - 90f, RowHeight);
                string label = doomed.HasCustomComponents
                    ? $"{doomed.Object.name}   —   {doomed.CustomComponents}"
                    : doomed.Object.name;
                EditorGUI.LabelField(labelRect, label);

                var pingRect = new Rect(rect.xMax - 64f, rect.y + 1f, 60f, RowHeight - 2f);
                if (GUI.Button(pingRect, "Select", EditorStyles.miniButton))
                {
                    Selection.activeGameObject = doomed.Object;
                    EditorGUIUtility.PingObject(doomed.Object);
                }
            }

            GUI.EndScrollView();
        }

        private void DrawFooter()
        {
            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(
                    $"Protected: {protectedObjects.Count:N0} (kept, coarse renderer removed)",
                    EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();

                if (GUILayout.Button("Cancel", GUILayout.Width(90f)))
                {
                    Close();
                    return;
                }

                // Nothing to dissolve is a real state (the window opens on the all-dynamic
                // case so the opt-in is reachable); pressing Convert there would be a no-op.
                using (new EditorGUI.DisabledScope(plan.GroupCount == 0))
                {
                    if (GUILayout.Button("Convert", GUILayout.Width(110f)))
                        RunConvert();
                }
            }
        }

        private void RunConvert()
        {
            bool recordUndo = true;
            if (StaticInstanceLodGroupConverter.ExceedsUndoThreshold(plan))
            {
                recordUndo = !EditorUtility.DisplayDialog(
                    "Convert LOD Groups",
                    $"This touches {plan.KeptRenderers + plan.RemovedRenderers:N0} renderers.\n\n" +
                    "Recording undo for that many objects is slow and may exhaust the undo " +
                    "buffer, leaving it only partly usable anyway.\n\n" +
                    "Proceed without undo recording? (Commit or save first if you want a way back.)",
                    "Convert without undo", "Convert with undo");
            }

            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            int forced = plan.Forced.Count;
            int dissolved = StaticInstanceLodGroupConverter.Execute(plan, recordUndo, protectedObjects);
            // After the dissolve, because the derivation is about what the conversion
            // just destroyed. The window owns the choice (it must survive Reanalyze); the
            // Plan is all Execute sees, so it is handed over here, right before the write.
            plan.DeriveShadowCastMaxLevel = deriveShadowCastMaxLevel;
            int shadowWrites =
                StaticInstanceLodGroupConverter.ApplyShadowCastDerivation(plan, recordUndo);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
            Debug.Log(
                $"[SI] Convert LOD Groups: dissolved {dissolved:N0} in '{scene.name}' " +
                $"(kept {plan.KeptRenderers:N0} LOD0 renderers, removed {plan.RemovedRenderers:N0}, " +
                $"protected {protectedObjects.Count:N0}, converted despite dynamic evidence " +
                $"{forced:N0}; undo " +
                $"{(recordUndo ? "recorded" : "NOT recorded")}" +
                (deriveShadowCastMaxLevel
                    ? $"; Shadow Cast Max Level written to {shadowWrites:N0} of " +
                      $"{plan.ShadowIntent.Count:N0} owner(s), each on its own group majority"
                    : string.Empty) +
                "). Scene left dirty — save to persist.");
            Close();
        }

        private static string Describe(Dictionary<string, int> counts)
        {
            var parts = new List<string>(counts.Count);
            foreach (KeyValuePair<string, int> pair in counts)
                parts.Add($"{pair.Key} x{pair.Value:N0}");
            parts.Sort();
            return string.Join(", ", parts);
        }
    }
}
