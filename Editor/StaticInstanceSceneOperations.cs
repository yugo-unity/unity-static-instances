using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StaticInstances.Editor
{
    // The source-bake stage only. Generated-material publication must finish
    // before the batch coordinator calls Save. No automatic scene save.
    internal static class StaticInstanceSceneOperations
    {
        internal readonly struct BakeResult
        {
            internal readonly bool Succeeded;
            internal readonly int BakedTargets;
            internal readonly string Error;

            internal BakeResult(bool succeeded, int bakedTargets, string error = null)
            {
                Succeeded = succeeded;
                BakedTargets = bakedTargets;
                Error = error;
            }
        }

        internal static List<StaticInstanceScatter> FindTargets(Scene scene)
        {
            var result = new List<StaticInstanceScatter>();
            if (!scene.IsValid() || !scene.isLoaded)
                return result;
            foreach (GameObject root in scene.GetRootGameObjects())
                result.AddRange(root.GetComponentsInChildren<StaticInstanceScatter>(true));
            result.Sort((left, right) => left.GetEntityId().CompareTo(right.GetEntityId()));
            return result;
        }

        internal static BakeResult Bake(Scene scene)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode ||
                !scene.IsValid() || !scene.isLoaded || EditorSceneManager.IsPreviewScene(scene))
                return new BakeResult(false, 0, "Bake requires a loaded authoring scene in Edit mode.");

            List<StaticInstanceScatter> targets = FindTargets(scene);
            // Validate the entire scene before changing any owner.
            foreach (StaticInstanceScatter target in targets)
            {
                var issues = StaticInstanceBakeUtility.ValidateForBake(target);
                StaticInstanceBakeUtility.LogIssues(target, issues);
                if (issues.Exists(StaticInstanceBakeUtility.IsBlockingError))
                    return new BakeResult(false, 0, $"Validation failed for '{target.name}'.");
            }
            if (targets.Count == 0)
                return new BakeResult(true, 0);

            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Bake Static Instances Scene");
            int baked = 0;
            try
            {
                foreach (StaticInstanceScatter target in targets)
                {
                    Undo.RegisterCompleteObjectUndo(target, "Bake Static Instances Scene");
                    if (!StaticInstanceBakeUtility.BakeGroup(target))
                    {
                        Undo.RevertAllDownToGroup(undoGroup);
                        return new BakeResult(false, 0, $"Bake failed for '{target.name}'.");
                    }
                    baked++;
                }
                EditorSceneManager.MarkSceneDirty(scene);
                Undo.CollapseUndoOperations(undoGroup);
                return new BakeResult(true, baked);
            }
            catch (Exception exception)
            {
                Undo.RevertAllDownToGroup(undoGroup);
                return new BakeResult(false, 0, exception.Message);
            }
        }

        internal static bool Save(Scene scene)
        {
            // Unsaved scenes require a user-selected path, never a batch save dialog.
            return scene.IsValid() && scene.isLoaded && !string.IsNullOrEmpty(scene.path) &&
                   EditorSceneManager.SaveScene(scene);
        }

        internal static string[] FindProjectScenes()
        {
            var paths = new List<string>();
            foreach (string guid in AssetDatabase.FindAssets("t:Scene", new[] { "Assets" }))
                paths.Add(AssetDatabase.GUIDToAssetPath(guid));
            paths.Sort(StringComparer.OrdinalIgnoreCase);
            return paths.ToArray();
        }
    }
}
