using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace StaticInstances.Editor
{
    internal sealed class StaticInstanceOperationsWindow : EditorWindow
    {
        private const float ButtonHeight = 20f;
        private static readonly GUIContent SectionHeader = new GUIContent("Scene Operations");
        private static readonly GUIContent SectionHelp = new GUIContent(
            "Bake, validate and mesh generation use loaded scenes. Clear and LOD conversion use the active scene.");
        private const int MaxListedTargets = 200;
        private Vector2 scroll;
        [SerializeField] private List<string> selectedScenes = new();

        // The last Validate press, listed under the batch section with the objects
        // behind each finding. Not serialized: the references are scene objects, so
        // a domain reload drops the list and the next Validate press rebuilds it.
        private static List<StaticInstanceBakeUtility.ValidationResult> validationResults;
        private static bool showInfoFindings;
        private static readonly HashSet<string> expandedFindings = new();
        private string[] scenePaths = Array.Empty<string>();

        private void OnEnable()
        {
            RefreshScenes();
            EditorApplication.projectChanged += RefreshScenes;
            EditorApplication.update += Repaint;
        }

        private void OnDisable()
        {
            EditorApplication.projectChanged -= RefreshScenes;
            EditorApplication.update -= Repaint;
        }

        private void RefreshScenes()
        {
            scenePaths = AssetDatabase.FindAssets("t:Scene").Select(AssetDatabase.GUIDToAssetPath)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
            selectedScenes.RemoveAll(path => !scenePaths.Contains(path));
            Repaint();
        }

        [MenuItem("Window/Rendering/Static Instances")]
        internal static void Open()
        {
            var window = GetWindow<StaticInstanceOperationsWindow>("Static Instances");
            window.minSize = new Vector2(360f, 320f);
            window.Show();
        }

        private static float HelpHeight(float width) =>
            EditorStyles.helpBox.CalcHeight(SectionHelp, Mathf.Max(80f, width));

        private void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            float width = Mathf.Max(300f, position.width - 32f);
            float height = HelpHeight(width) + 2f * EditorGUIUtility.singleLineHeight +
                3f * ButtonHeight + 8f * EditorGUIUtility.standardVerticalSpacing;
            Rect area = GUILayoutUtility.GetRect(width, height);
            area.x += 6f;
            area.width = width;
            DrawCurrentSceneSection(ref area);
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Selected Scenes", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Bake, generate wind-stripped SpeedTree materials, and save checked scenes in order.", MessageType.Info);
            using (new EditorGUI.DisabledScope(StaticInstanceSceneBatch.IsRunning || EditorApplication.isPlayingOrWillChangePlaymode))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Select All"))
                        selectedScenes = scenePaths.Where(path => path.StartsWith("Assets/", StringComparison.Ordinal)).ToList();
                    if (GUILayout.Button("Select None"))
                        selectedScenes.Clear();
                    if (GUILayout.Button("Refresh"))
                        RefreshScenes();
                }
                foreach (string path in scenePaths)
                {
                    bool writable = path.StartsWith("Assets/", StringComparison.Ordinal);
                    using (new EditorGUI.DisabledScope(!writable))
                    {
                        bool selected = selectedScenes.Contains(path);
                        bool next = EditorGUILayout.ToggleLeft(path + (writable ? "" : " (package, read-only)"), selected);
                        if (next && !selected) selectedScenes.Add(path);
                        if (!next && selected) selectedScenes.Remove(path);
                    }
                }
                using (new EditorGUI.DisabledScope(selectedScenes.Count == 0))
                    if (GUILayout.Button("Bake Selected Scenes"))
                        StaticInstanceSceneBatch.Start(selectedScenes.ToArray(), saveResults: true);
            }
            if (StaticInstanceSceneBatch.IsRunning && GUILayout.Button("Cancel After Current Operation"))
                StaticInstanceSceneBatch.Cancel();
            if (!string.IsNullOrEmpty(StaticInstanceSceneBatch.Status))
                EditorGUILayout.HelpBox(StaticInstanceSceneBatch.Status, MessageType.None);
            DrawValidationResults();
            EditorGUILayout.EndScrollView();
        }

        private static void DrawValidationResults()
        {
            if (validationResults == null)
                return;

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Validation Results", EditorStyles.boldLabel);
                showInfoFindings = GUILayout.Toggle(
                    showInfoFindings, "Show Info", EditorStyles.miniButton, GUILayout.Width(70f));
                if (GUILayout.Button("Clear", EditorStyles.miniButton, GUILayout.Width(50f)))
                {
                    validationResults = null;
                    expandedFindings.Clear();
                    return;
                }
            }

            int shownGroups = 0;
            foreach (StaticInstanceBakeUtility.ValidationResult result in validationResults)
            {
                // Destroyed, or its scene was closed since the press.
                if (result.Group == null)
                    continue;

                bool headerDrawn = false;
                for (int i = 0; i < result.Issues.Count; i++)
                {
                    StaticInstanceBakeUtility.Issue issue = result.Issues[i];
                    bool problem = issue.Severity == LogType.Error || issue.Severity == LogType.Warning;
                    if (!problem && !showInfoFindings)
                        continue;

                    if (!headerDrawn)
                    {
                        headerDrawn = true;
                        shownGroups++;
                        DrawTargetRow(result.Group, 0f, EditorStyles.boldLabel);
                    }

                    MessageType messageType = issue.Severity switch
                    {
                        LogType.Error => MessageType.Error,
                        LogType.Warning => MessageType.Warning,
                        _ => MessageType.Info,
                    };

                    if (issue.Targets.Length == 0)
                    {
                        EditorGUILayout.HelpBox(issue.Message, messageType);
                        continue;
                    }

                    // The finding's own box is the toggle for its object list.
                    string key = result.Group.GetHashCode() + "|" + i;
                    bool expanded = expandedFindings.Contains(key);
                    string hint = expanded
                        ? "Click to hide the objects."
                        : $"Click to show the {issue.Targets.Length} object(s).";
                    EditorGUILayout.HelpBox(issue.Message + "\n" + hint, messageType);
                    Rect box = GUILayoutUtility.GetLastRect();
                    EditorGUIUtility.AddCursorRect(box, MouseCursor.Link);
                    Event current = Event.current;
                    // `expanded` keeps its old value for the rest of this
                    // event, so the rows below match the Layout pass; the
                    // next repaint shows the new state.
                    if (current.type == EventType.MouseDown && current.button == 0 &&
                        box.Contains(current.mousePosition))
                    {
                        if (expanded) expandedFindings.Remove(key);
                        else expandedFindings.Add(key);
                        current.Use();
                    }

                    if (!expanded)
                        continue;

                    int listed = Mathf.Min(issue.Targets.Length, MaxListedTargets);
                    for (int t = 0; t < listed; t++)
                        DrawTargetRow(issue.Targets[t], 16f, EditorStyles.label);
                    if (issue.Targets.Length > listed)
                        EditorGUILayout.LabelField($"    … and {issue.Targets.Length - listed} more");
                }
            }

            if (shownGroups == 0)
            {
                EditorGUILayout.HelpBox(
                    showInfoFindings ? "No findings." : "No errors or warnings.", MessageType.Info);
            }
        }

        // One clickable row: selects and pings the object, labelled with its
        // hierarchy path so same-named sources can be told apart.
        private static void DrawTargetRow(UnityEngine.Object target, float indent, GUIStyle style)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(indent);
                if (target == null)
                {
                    EditorGUILayout.LabelField("(destroyed since validation)", EditorStyles.miniLabel);
                    return;
                }

                var content = new GUIContent(
                    HierarchyPath(target),
                    AssetPreview.GetMiniThumbnail(target),
                    "Click to select and ping.");
                if (GUILayout.Button(content, style, GUILayout.Height(EditorGUIUtility.singleLineHeight)))
                {
                    Selection.activeObject = target;
                    EditorGUIUtility.PingObject(target);
                }
            }
        }

        private static string HierarchyPath(UnityEngine.Object target)
        {
            Transform node = target switch
            {
                Component component => component.transform,
                GameObject gameObject => gameObject.transform,
                _ => null,
            };
            if (node == null)
                return target.name;

            var parts = new List<string>();
            for (; node != null; node = node.parent)
                parts.Add(node.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static void DrawCurrentSceneSection(ref Rect position)
        {
            position.y += EditorGUIUtility.standardVerticalSpacing;
            position.height = EditorGUIUtility.singleLineHeight;
            EditorGUI.LabelField(position, SectionHeader, EditorStyles.boldLabel);
            position.y += position.height + EditorGUIUtility.standardVerticalSpacing;

            position.height = HelpHeight(position.width);
            EditorGUI.HelpBox(position, SectionHelp.text, MessageType.None);
            position.y += position.height + EditorGUIUtility.standardVerticalSpacing;

            // Disabled in Play Mode (baking mutates the scene, and a Play-Mode
            // mutation is discarded on exit) and with no loaded scene to act
            // on. A scene that exists but holds no targets is NOT a disable
            // condition — the operations report "nothing to do", which is a
            // more useful answer than a greyed-out button.
            var active = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            bool usable = !StaticInstanceSceneBatch.IsRunning && !EditorApplication.isPlayingOrWillChangePlaymode && active.IsValid();

            using (new EditorGUI.DisabledScope(!usable))
            {
                position.height = ButtonHeight;
                Rect row = position;
                float half = (row.width - 4f) / 2f;
                row.width = half;

                if (GUI.Button(row, new GUIContent(
                        "Bake Loaded Sources",
                        "Bake all loaded scenes and save the active scene. Save other modified scenes separately.")))
                {
                    StaticInstanceBakeUtility.BakeAllInScene();
                }

                row.x += half + 4f;
                if (GUI.Button(row, new GUIContent(
                        "Validate",
                        "Report bake issues across loaded scenes without changing them.")))
                {
                    validationResults = StaticInstanceBakeUtility.ValidateAllInSceneWithResults();
                    expandedFindings.Clear();
                }

                // Its own row: it is the only destructive action in this section and
                // should not sit beside the three that build things.
                position.y += position.height + EditorGUIUtility.standardVerticalSpacing;
                if (GUI.Button(position, new GUIContent(
                        "Clear Baked Data",
                        "Clear bakes in the active scene and save it. After a successful save, delete unreferenced wind-static materials; asset deletion cannot be undone.")))
                {
                    // Confirmed here and not inside `ClearAllInScene`: deleting a generated
                    // material is not undoable, so the press needs a confirmation, but the
                    // function stays modal-free so editor automation can call it.
                    if (EditorUtility.DisplayDialog(
                            "Clear baked data in the active scene?",
                            "Every Static Instance bake in the ACTIVE scene is removed and " +
                            "the scene is saved — or, if the save fails, the clear stays " +
                            "in memory and no clone is collected. Additively loaded " +
                            "scenes are not touched." +
                            "\n\nWind-stripped material clones this scene was keeping " +
                            "alive are deleted too — clones anything else still references " +
                            "are kept. Component clears can be undone; the deleted " +
                            "material assets cannot.",
                            "Clear",
                            "Cancel"))
                    {
                        StaticInstanceBakeUtility.ClearAllInScene();
                    }
                }

                position.y += position.height + EditorGUIUtility.standardVerticalSpacing * 2f;
                DrawConvertRow(ref position);
            }

            position.y += position.height + EditorGUIUtility.standardVerticalSpacing;
        }

        // One-shot conversion input, never persisted: it exists only to turn
        // the source LOD Groups' screen-relative heights into metric
        // distances, and once converted the result stops tracking it. Seeded
        // from the Main Camera so the common case needs no thought.
        private static float conversionFov;

        private static void DrawConvertRow(ref Rect position)
        {
            if (conversionFov <= 0f)
            {
                Camera main = Camera.main;
                conversionFov = main != null ? main.fieldOfView : 60f;
            }

            position.height = EditorGUIUtility.singleLineHeight;
            // Keep the conversion input visually grouped with its two actions. It is
            // the only labelled field in this section, so unindented it would read as
            // belonging to no section. The help box and button rows stay full width.
            using (new EditorGUI.IndentLevelScope())
            {
                conversionFov = EditorGUI.Slider(
                    position,
                    new GUIContent(
                        "Convert FOV",
                        "Vertical FOV for converting LODGroup screen heights to metric distances. This one-time input is not saved."),
                    conversionFov, 10f, 120f);
            }
            position.y += position.height + EditorGUIUtility.standardVerticalSpacing;

            position.height = ButtonHeight;
            Rect row = position;
            float half = (row.width - 4f) / 2f;
            row.width = half;
            if (GUI.Button(row, new GUIContent(
                    "Convert LOD Groups…",
                    "Preview active-scene conversion that keeps LOD0 and removes coarser renderers. Save the scene after applying.")))
            {
                RunConversion();
            }

            // Stage two of the same job, a separate press because it touches assets:
            // the dissolve removes the coarse renderers on the promise that Mesh LODs
            // replace them, and this makes that true. Ordered as they are run.
            row.x += half + 4f;
            if (GUI.Button(row, new GUIContent(
                    "Generate Mesh LODs…",
                    "Preview mesh LOD generation for SIS in loaded scenes. Asset changes cannot be undone; save scene reference changes.")))
            {
                RunMeshLodGeneration();
            }
        }

        // Analysis only — the window decides whether anything happens.
        private static void RunConversion()
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            StaticInstanceLodGroupConverter.Plan plan =
                StaticInstanceLodGroupConverter.Analyze(scene.GetRootGameObjects(), conversionFov);

            // Only the genuinely empty case is a dialog. A scene whose groups were all
            // refused as dynamic still opens the window, where the per-group
            // "convert anyway" opt-in lives.
            if (plan.IsEmpty && plan.Skipped.Count == 0)
            {
                EditorUtility.DisplayDialog(
                    "Convert LOD Groups",
                    $"No LOD Groups in '{scene.name}' — this scene is already free of them.",
                    "OK");
                return;
            }

            StaticInstanceConvertPreviewWindow.Open(plan, conversionFov, scene.name);
        }

        // Analysis only, like the conversion: the window decides whether anything
        // is written. Opened even with nothing to do, so it can state the counts
        // behind "every mesh already has its chain".
        private static void RunMeshLodGeneration()
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            StaticInstanceMeshLodGenerator.Plan plan =
                StaticInstanceMeshLodGenerator.Analyze(
                    StaticInstanceMeshLodGenerator.DefaultOutputFolder(scene));

            if (plan.OwnerCount == 0)
            {
                EditorUtility.DisplayDialog(
                    "Generate Mesh LODs",
                    $"No Static Instance components in '{scene.name}'. This acts on the meshes " +
                    "they use, so there is nothing in scope.",
                    "OK");
                return;
            }

            // FindBakeTargetsInScene spans every loaded scene, so the scope line says so.
            int loaded = UnityEngine.SceneManagement.SceneManager.loadedSceneCount;
            string scope = loaded > 1
                ? $"{scene.name} + {loaded - 1} other loaded scene(s)"
                : scene.name;
            StaticInstanceMeshLodPreviewWindow.Open(plan, scope);
        }
    }
}
