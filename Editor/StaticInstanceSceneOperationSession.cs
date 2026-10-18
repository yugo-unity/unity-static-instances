using System;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace StaticInstances.Editor
{
    // Short-lived Edit-mode scene traversal guard. A coordinator that spans
    // domain reloads persists its own setup; this guard does not.
    internal sealed class StaticInstanceSceneOperationSession
    {
        private readonly SceneSetup[] originalSetup;

        internal StaticInstanceSceneOperationSession(SceneSetup[] setup) => originalSetup = setup;

        internal static bool TryBegin(Func<bool> saveModifiedScenes,
            out StaticInstanceSceneOperationSession session, out string error)
        {
            session = null;
            error = null;
            if (!saveModifiedScenes())
            {
                error = "Scene save was canceled.";
                return false;
            }
            // Unity's save prompt can return true for Don't Save. Do not treat
            // that as permission to discard edits during later scene switches.
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene.isDirty || string.IsNullOrEmpty(scene.path))
                {
                    error = "Save the open scenes before starting scene traversal.";
                    return false;
                }
            }
            session = new StaticInstanceSceneOperationSession(EditorSceneManager.GetSceneManagerSetup());
            return true;
        }

        internal bool TryRestore(out string error)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                if (!SceneManager.GetSceneAt(i).isDirty)
                    continue;
                error = "The current scene has unsaved changes and remains open. Save or discard them before restoring the previous scene setup.";
                return false;
            }
            try
            {
                EditorSceneManager.RestoreSceneManagerSetup(originalSetup);
                error = null;
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }
    }
}
