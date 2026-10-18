using UnityEditor;
using UnityEngine;

namespace StaticInstances.Editor
{
    // Preview for the mesh LOD generation. A window rather than a dialog:
    // the importer flags and generated assets cannot be undone, so the user
    // needs to see which assets will change before confirming.
    //
    // Nothing here mutates anything. The Plan comes from a pure analysis pass
    // and only reaches Execute when Generate is pressed.
    internal sealed class StaticInstanceMeshLodPreviewWindow : EditorWindow
    {
        private StaticInstanceMeshLodGenerator.Plan plan;
        private string sceneName;
        private string outputFolder;
        private Vector2 scroll;
        private bool showUnchanged;

        private const float RowHeight = 18f;

        internal static void Open(
            StaticInstanceMeshLodGenerator.Plan plan, string sceneName)
        {
            var window = GetWindow<StaticInstanceMeshLodPreviewWindow>(true, "Generate Mesh LODs", true);
            window.plan = plan;
            window.sceneName = sceneName;
            window.outputFolder = plan.OutputFolder;
            window.minSize = new Vector2(560f, 400f);
            window.Show();
        }

        private void OnGUI()
        {
            if (plan == null)
            {
                EditorGUILayout.HelpBox(
                    "No plan — reopen from Graphics Settings.", MessageType.Info);
                return;
            }

            DrawHeader();
            EditorGUILayout.Space();
            DrawList();
            DrawFooter();
        }

        private void DrawHeader()
        {
            EditorGUILayout.LabelField($"Scope: {sceneName}", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                $"{plan.Filters.Count:N0} distinct mesh(es) under " +
                $"{plan.OwnerCount:N0} Static Instance component(s)");

            int flag = plan.Count(StaticInstanceMeshLodGenerator.MeshAction.FlagImporter);
            int copy = plan.Count(StaticInstanceMeshLodGenerator.MeshAction.CreateCopy);
            int repoint = plan.Count(StaticInstanceMeshLodGenerator.MeshAction.RepointOnly);
            int chained = plan.Count(StaticInstanceMeshLodGenerator.MeshAction.AlreadyChained);
            int exhausted = plan.Count(StaticInstanceMeshLodGenerator.MeshAction.Exhausted);
            int unreachable = plan.Count(StaticInstanceMeshLodGenerator.MeshAction.Unreachable);

            EditorGUILayout.LabelField(
                $"Flag {flag:N0} importer(s) · generate {copy:N0} copy asset(s) · " +
                $"re-point {plan.RepointCount:N0} MeshFilter(s)" +
                (repoint > 0 ? $" ({repoint:N0} of them onto copies that already exist)" : string.Empty));
            EditorGUILayout.LabelField(
                $"Leaving alone: {chained:N0} already chained · {exhausted:N0} already processed " +
                $"and still single-level · {unreachable:N0} not an asset",
                EditorStyles.miniLabel);

            if (!plan.HasWork)
            {
                EditorGUILayout.HelpBox(
                    "Nothing to do — every mesh in scope already has its chain, or cannot be " +
                    "given one. Re-running this is always safe; it is a no-op in this state.",
                    MessageType.Info);
            }

            // State the two halves separately: scene edits are undoable, asset changes are not.
            EditorGUILayout.HelpBox(
                "Importer flags and generated mesh assets are ASSET changes and CANNOT be " +
                "undone — commit or back up the project first.\n\n" +
                "The MeshFilter re-points are scene changes and undo in one step. The scene is " +
                "left dirty; save it to keep them.",
                MessageType.Warning);

            if (exhausted > 0)
            {
                EditorGUILayout.HelpBox(
                    $"{exhausted:N0} mesh(es) have already been through this — importer flagged, " +
                    "or a copy generated here — and still come back with a single level, usually " +
                    "too few triangles to decimate. They render at their only level, which is " +
                    "correct, not an error.",
                    MessageType.Info);
            }

            EditorGUI.BeginChangeCheck();
            outputFolder = EditorGUILayout.TextField(
                new GUIContent(
                    "Copy asset folder",
                    "Folder for generated mesh copies, created if needed. Changing it can generate copies again."),
                outputFolder);
            if (EditorGUI.EndChangeCheck() && !string.IsNullOrWhiteSpace(outputFolder))
                plan = StaticInstanceMeshLodGenerator.Analyze(outputFolder.Trim());
        }

        private void DrawList()
        {
            showUnchanged = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    "Also list the meshes nothing happens to",
                    "Off shows only what this run changes."),
                showUnchanged);

            Rect viewport = GUILayoutUtility.GetRect(
                0f, position.height, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));

            int visible = 0;
            foreach (StaticInstanceMeshLodGenerator.MeshPlan entry in plan.Meshes)
            {
                if (showUnchanged || IsChange(entry.Action))
                    visible++;
            }

            var content = new Rect(0f, 0f, viewport.width - 20f, visible * RowHeight);
            scroll = GUI.BeginScrollView(viewport, scroll, content);

            int row = 0;
            foreach (StaticInstanceMeshLodGenerator.MeshPlan entry in plan.Meshes)
            {
                if (!showUnchanged && !IsChange(entry.Action))
                    continue;

                var rect = new Rect(0f, row * RowHeight, content.width, RowHeight);
                row++;
                // Only the rows on screen are drawn: a converted scene can
                // reference thousands of distinct meshes and IMGUI does not
                // survive drawing a control for each one per repaint.
                if (rect.yMax < scroll.y || rect.y > scroll.y + viewport.height)
                    continue;

                var actionRect = new Rect(rect.x, rect.y, 150f, rect.height);
                EditorGUI.LabelField(actionRect, Describe(entry.Action), EditorStyles.miniLabel);

                var labelRect = new Rect(rect.x + 154f, rect.y, rect.width - 220f, rect.height);
                string mesh = entry.Mesh != null ? entry.Mesh.name : "(missing)";
                EditorGUI.LabelField(
                    labelRect,
                    new GUIContent($"{mesh}  ×{entry.FilterCount:N0}", entry.SourcePath));

                if (entry.Mesh == null)
                    continue;

                var pingRect = new Rect(rect.xMax - 62f, rect.y, 60f, rect.height - 2f);
                if (GUI.Button(pingRect, "Select", EditorStyles.miniButton))
                {
                    Selection.activeObject = entry.Mesh;
                    EditorGUIUtility.PingObject(entry.Mesh);
                }
            }

            GUI.EndScrollView();
        }

        private static bool IsChange(StaticInstanceMeshLodGenerator.MeshAction action)
        {
            return action == StaticInstanceMeshLodGenerator.MeshAction.FlagImporter ||
                action == StaticInstanceMeshLodGenerator.MeshAction.CreateCopy ||
                action == StaticInstanceMeshLodGenerator.MeshAction.RepointOnly;
        }

        private static string Describe(StaticInstanceMeshLodGenerator.MeshAction action)
        {
            switch (action)
            {
                case StaticInstanceMeshLodGenerator.MeshAction.FlagImporter:
                    return "flag importer";
                case StaticInstanceMeshLodGenerator.MeshAction.CreateCopy:
                    return "copy + re-point";
                case StaticInstanceMeshLodGenerator.MeshAction.RepointOnly:
                    return "re-point only";
                case StaticInstanceMeshLodGenerator.MeshAction.Exhausted:
                    return "single level";
                case StaticInstanceMeshLodGenerator.MeshAction.Unreachable:
                    return "not an asset";
                default:
                    return "already chained";
            }
        }

        private void DrawFooter()
        {
            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Cancel", GUILayout.Width(90f)))
                {
                    Close();
                    return;
                }

                using (new EditorGUI.DisabledScope(!plan.HasWork))
                {
                    if (GUILayout.Button("Generate", GUILayout.Width(110f)))
                        Run();
                }
            }
        }

        private void Run()
        {
            // Same threshold reasoning as the converter's: past a few thousand
            // objects, recording undo costs more than it is worth and can
            // exhaust the buffer anyway, so the choice is the user's.
            bool recordUndo = true;
            if (plan.RepointCount > 2000)
            {
                recordUndo = !EditorUtility.DisplayDialog(
                    "Generate Mesh LODs",
                    $"This re-points {plan.RepointCount:N0} MeshFilters.\n\n" +
                    "Recording undo for that many objects is slow and may exhaust the undo " +
                    "buffer, leaving it only partly usable anyway. The asset changes are not " +
                    "undoable either way.\n\n" +
                    "Proceed without undo recording?",
                    "Generate without undo", "Generate with undo");
            }

            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            StaticInstanceMeshLodGenerator.Result result =
                StaticInstanceMeshLodGenerator.Execute(plan, recordUndo);
            if (result.FiltersRepointed > 0)
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);

            Debug.Log(
                $"[SI] Generate Mesh LODs in '{scene.name}': " +
                $"{result.ImportersFlagged:N0} importer(s) flagged, " +
                $"{result.CopiesCreated:N0} copy asset(s) written to {plan.OutputFolder}, " +
                $"{result.FiltersRepointed:N0} MeshFilter(s) re-pointed (undo " +
                $"{(recordUndo ? "recorded for the re-points" : "NOT recorded")}). " +
                "Asset changes are permanent; save the scene to keep the re-points.");
            Close();
        }
    }
}
