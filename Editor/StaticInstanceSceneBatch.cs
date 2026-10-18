using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StaticInstances.Editor
{
    // Bakes the checked scenes one per editor tick: open, bake, generate the
    // wind-stripped SpeedTree materials, save. The job lives in SessionState so
    // a domain reload between scenes resumes it.
    [InitializeOnLoad]
    internal static class StaticInstanceSceneBatch
    {
        internal static Func<Scene, bool> SaveScene = StaticInstanceSceneOperations.Save;
        private const string JobKey = "StaticInstances.SceneBatch.Job";
        private const string StatusKey = "StaticInstances.SceneBatch.Status";
        [Serializable] private sealed class Setup { public string Path; public bool Loaded; public bool Active; }
        [Serializable] private sealed class Job
        {
            public string[] Paths;
            public int Index;
            public Setup[] Original;
            public bool SaveResults;
            public bool Canceled;
        }

        static StaticInstanceSceneBatch()
        {
            EditorApplication.update += Tick;
        }

        internal static bool IsRunning => !string.IsNullOrEmpty(SessionState.GetString(JobKey, ""));
        internal static string Status => SessionState.GetString(StatusKey, "");
        private static Job Read() => JsonUtility.FromJson<Job>(SessionState.GetString(JobKey, ""));
        private static void Store(Job job) => SessionState.SetString(JobKey, JsonUtility.ToJson(job));
        private static void Report(string message) => SessionState.SetString(StatusKey, message);

        internal static bool Start(string[] paths, bool saveResults, bool prompt = true)
        {
            if (IsRunning || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
                return false;
            paths = paths?.Distinct().OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
            if (paths == null || paths.Length == 0 || paths.Any(path =>
                    !path.StartsWith("Assets/", StringComparison.Ordinal) ||
                    AssetDatabase.LoadAssetAtPath<SceneAsset>(path) == null))
            {
                Report("Select saved project scenes under Assets.");
                return false;
            }
            bool SaveOpen()
            {
                if (!prompt)
                    return true;
                if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                    return false;
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    Scene scene = SceneManager.GetSceneAt(i);
                    if (string.IsNullOrEmpty(scene.path) && !EditorSceneManager.SaveScene(scene))
                        return false;
                }
                return true;
            }
            if (!StaticInstanceSceneOperationSession.TryBegin(SaveOpen, out _, out string error))
            {
                Report(error);
                return false;
            }
            var job = new Job
            {
                Paths = paths,
                Original = EditorSceneManager.GetSceneManagerSetup().Select(setup => new Setup
                { Path = setup.path, Loaded = setup.isLoaded, Active = setup.isActive }).ToArray(),
                SaveResults = saveResults,
            };
            Store(job);
            Report($"Preparing {paths.Length} scene(s).");
            return true;
        }

        internal static void Cancel()
        {
            if (!IsRunning)
                return;
            Job job = Read();
            job.Canceled = true;
            Store(job);
            if (!EditorApplication.isPlayingOrWillChangePlaymode)
                Finish(job, "Canceled. Previously saved scenes remain saved.");
        }

        private static void Tick()
        {
            if (!IsRunning || EditorApplication.isCompiling || EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            Job job = Read();
            try
            {
                if (job.Canceled)
                {
                    Finish(job, "Canceled. Previously saved scenes remain saved.");
                    return;
                }
                ProcessScene(job);
            }
            catch (Exception exception)
            {
                Finish(job, exception.Message);
                Debug.LogException(exception);
            }
        }

        private static void ProcessScene(Job job)
        {
            Scene scene = EditorSceneManager.OpenScene(job.Paths[job.Index], OpenSceneMode.Single);
            Report($"{job.Index + 1}/{job.Paths.Length}: baking {scene.name}");
            var result = StaticInstanceSceneOperations.Bake(scene);
            if (!result.Succeeded)
            {
                Finish(job, result.Error);
                return;
            }
            StaticInstanceWindStaticMaterials.GenerateForScene(scene);
            if (job.SaveResults && !SaveScene(scene))
            {
                Finish(job, $"Could not save '{scene.path}'. Its edited scene remains open.");
                return;
            }
            if (++job.Index == job.Paths.Length)
            {
                Finish(job, job.SaveResults
                    ? $"Completed and saved {job.Paths.Length} scene(s)."
                    : "Bake complete. Save the scene to keep the result.");
                return;
            }
            Store(job);
        }

        private static bool RestoreAroundEditedSource(Job job, SceneSetup[] setup)
        {
            Scene source = SceneManager.GetSceneByPath(job.Paths[0]);
            if (!source.IsValid() || !source.isLoaded)
                return false;
            foreach (SceneSetup entry in setup)
            {
                if (entry.path == source.path)
                    continue;
                Scene loaded = SceneManager.GetSceneByPath(entry.path);
                if (!loaded.IsValid())
                    EditorSceneManager.OpenScene(entry.path,
                        entry.isLoaded ? OpenSceneMode.Additive : OpenSceneMode.AdditiveWithoutLoading);
            }
            foreach (SceneSetup entry in setup)
                if (entry.isActive)
                    SceneManager.SetActiveScene(SceneManager.GetSceneByPath(entry.path));
            return true;
        }

        private static void Finish(Job job, string status)
        {
            try
            {
                var setup = job.Original.Select(entry => new SceneSetup
                    { path = entry.Path, isLoaded = entry.Loaded, isActive = entry.Active }).ToArray();
                var session = new StaticInstanceSceneOperationSession(setup);
                // Keep the unsaved edit of an unsaved single-scene run open
                // instead of discarding it by restoring the original setup.
                bool restored = !job.SaveResults && job.Index == job.Paths.Length &&
                                RestoreAroundEditedSource(job, setup);
                if (!restored && !session.TryRestore(out string restoreError))
                    status += " " + restoreError;
            }
            catch (Exception exception)
            {
                status += " Scene restoration failed: " + exception.Message;
            }
            finally
            {
                SessionState.EraseString(JobKey);
                Report(status);
            }
        }
    }
}
