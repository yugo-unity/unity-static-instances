using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace StaticInstances.Editor
{
    // Supplies the mesh LOD chains that replace the coarse renderers removed by
    // the LOD Group dissolve.
    //
    // Two routes, because one does not cover the content:
    //   Route A: a mesh imported by ModelImporter (FBX and friends) gets
    //        generateMeshLods on the importer, and Unity builds the chain.
    //   Route B: a mesh from a ScriptedImporter (e.g. SpeedTree .st9) has no such
    //        switch, so a copy asset is baked with
    //        MeshLodUtility.GenerateMeshLods and every MeshFilter in scope is
    //        re-pointed at the copy.
    //
    // Scope is the Static Instance owners in the open scene, never the whole
    // scene: flagging an importer changes the asset for every user of it.
    // (A shared FBX still gains its chain everywhere; a LOD Group renders
    // level 0, so its visuals are unchanged.)
    //
    // Split into analysis and execution for the same reason the converter is:
    // the preview must report exactly what will happen, and asset changes are
    // the half that cannot be undone.
    internal static class StaticInstanceMeshLodGenerator
    {
        internal enum MeshAction
        {
            // Already has a chain — an untouched source that shipped with one,
            // or a copy a previous run generated. This is what makes a re-run
            // a no-op.
            AlreadyChained,
            // Route A: ModelImporter with generateMeshLods off.
            FlagImporter,
            // Everything this tool can do has been done and the mesh still has
            // one level (typically too few triangles to decimate): an importer
            // already flagged, or a copy this tool already generated. Kept apart
            // from AlreadyChained so a re-run neither re-flags nor re-copies.
            Exhausted,
            // Route B: no copy asset yet.
            CreateCopy,
            // Route B: the copy exists but these filters still point at the
            // source — the state a half-finished run leaves behind.
            RepointOnly,
            // No asset path at all (a procedural or embedded mesh). Nothing
            // this tool can do; reported so the count is honest.
            Unreachable,
        }

        internal readonly struct MeshPlan
        {
            public readonly Mesh Mesh;
            public readonly MeshAction Action;
            public readonly string SourcePath;
            // Destination for the Route B routes, empty otherwise.
            public readonly string CopyPath;
            public readonly int FilterCount;

            public MeshPlan(Mesh mesh, MeshAction action, string sourcePath, string copyPath, int filterCount)
            {
                Mesh = mesh;
                Action = action;
                SourcePath = sourcePath;
                CopyPath = copyPath;
                FilterCount = filterCount;
            }
        }

        internal sealed class Plan
        {
            public readonly List<MeshPlan> Meshes = new List<MeshPlan>();
            public readonly Dictionary<Mesh, List<MeshFilter>> Filters =
                new Dictionary<Mesh, List<MeshFilter>>();
            public string OutputFolder = string.Empty;
            public int OwnerCount;

            public int Count(MeshAction action)
            {
                int n = 0;
                foreach (MeshPlan mesh in Meshes)
                {
                    if (mesh.Action == action)
                        n++;
                }

                return n;
            }

            // Filters that will be re-pointed — the scene-side half of the
            // work, and the only half that can be undone.
            public int RepointCount
            {
                get
                {
                    int n = 0;
                    foreach (MeshPlan mesh in Meshes)
                    {
                        if (mesh.Action == MeshAction.CreateCopy ||
                            mesh.Action == MeshAction.RepointOnly)
                        {
                            n += mesh.FilterCount;
                        }
                    }

                    return n;
                }
            }

            public bool HasWork =>
                Count(MeshAction.FlagImporter) > 0 ||
                Count(MeshAction.CreateCopy) > 0 ||
                Count(MeshAction.RepointOnly) > 0;
        }

        internal readonly struct Result
        {
            public readonly int ImportersFlagged;
            public readonly int CopiesCreated;
            public readonly int FiltersRepointed;

            public Result(int importersFlagged, int copiesCreated, int filtersRepointed)
            {
                ImportersFlagged = importersFlagged;
                CopiesCreated = copiesCreated;
                FiltersRepointed = filtersRepointed;
            }
        }

        // Where a scene's generated copies go by default: beside the scene,
        // named after it. Deliberately not a persisted setting and not a
        // folder inside the package — generated content belongs with the
        // content that generated it, and the window lets it be changed before
        // anything is written.
        internal static string DefaultOutputFolder(UnityEngine.SceneManagement.Scene scene)
        {
            string scenePath = scene.path;
            if (string.IsNullOrEmpty(scenePath))
                return "Assets/StaticInstanceMeshLods";

            int slash = scenePath.LastIndexOf('/');
            string folder = slash > 0 ? scenePath.Substring(0, slash) : "Assets";
            return $"{folder}/{scene.name}_MeshLods";
        }

        internal static Plan Analyze(string outputFolder)
        {
            var plan = new Plan { OutputFolder = outputFolder };

            List<MonoBehaviour> owners = StaticInstanceBakeUtility.FindBakeTargetsInScene();
            plan.OwnerCount = owners.Count;
            foreach (MonoBehaviour owner in owners)
            {
                if (owner == null)
                    continue;

                foreach (MeshFilter filter in
                    StaticInstanceOwnership.OwnedComponents<MeshFilter>(owner))
                {
                    Mesh mesh = filter != null ? filter.sharedMesh : null;
                    if (mesh == null)
                        continue;

                    if (!plan.Filters.TryGetValue(mesh, out List<MeshFilter> list))
                    {
                        list = new List<MeshFilter>();
                        plan.Filters.Add(mesh, list);
                    }

                    list.Add(filter);
                }
            }

            foreach (KeyValuePair<Mesh, List<MeshFilter>> pair in plan.Filters)
                plan.Meshes.Add(Classify(pair.Key, pair.Value.Count, outputFolder));

            plan.Meshes.Sort((a, b) =>
            {
                int byAction = a.Action.CompareTo(b.Action);
                return byAction != 0
                    ? byAction
                    : string.CompareOrdinal(a.SourcePath, b.SourcePath);
            });
            return plan;
        }

        private static MeshPlan Classify(Mesh mesh, int filterCount, string outputFolder)
        {
            if (mesh.lodCount > 1)
                return new MeshPlan(mesh, MeshAction.AlreadyChained, PathOf(mesh), string.Empty, filterCount);

            string path = PathOf(mesh);
            if (string.IsNullOrEmpty(path))
                return new MeshPlan(mesh, MeshAction.Unreachable, path, string.Empty, filterCount);

            // A copy this tool wrote whose chain came back single-level: keeps
            // re-runs idempotent for meshes too small to decimate. Without it the
            // copy would be treated as an ordinary source and copied again.
            if (!string.IsNullOrEmpty(outputFolder) &&
                path.StartsWith(outputFolder + "/", System.StringComparison.Ordinal))
            {
                return new MeshPlan(mesh, MeshAction.Exhausted, path, string.Empty, filterCount);
            }

            if (AssetImporter.GetAtPath(path) is ModelImporter importer)
            {
                MeshAction action = importer.generateMeshLods
                    ? MeshAction.Exhausted
                    : MeshAction.FlagImporter;
                return new MeshPlan(mesh, action, path, string.Empty, filterCount);
            }

            string copyPath = CopyPathFor(path, mesh, outputFolder);
            bool exists = AssetDatabase.LoadAssetAtPath<Mesh>(copyPath) != null;
            return new MeshPlan(
                mesh,
                exists ? MeshAction.RepointOnly : MeshAction.CreateCopy,
                path,
                copyPath,
                filterCount);
        }

        private static string PathOf(Mesh mesh)
        {
            return AssetDatabase.GetAssetPath(mesh);
        }

        // Source path and mesh name both go into the file name: one .st9 can
        // hold several meshes, and two species can share a mesh name.
        private static string CopyPathFor(string sourcePath, Mesh mesh, string outputFolder)
        {
            return $"{outputFolder}/{Sanitize(sourcePath)}_{Sanitize(mesh.name)}.asset";
        }

        private static string Sanitize(string value)
        {
            var builder = new StringBuilder(value.Length);
            foreach (char c in value)
                builder.Append(char.IsLetterOrDigit(c) ? c : '_');
            return builder.ToString();
        }

        // `recordUndo` covers the MeshFilter re-points only. The generated
        // assets and the importer flags are not undoable at all, which is why
        // the window states the two halves apart.
        internal static Result Execute(Plan plan, bool recordUndo)
        {
            EnsureFolder(plan.OutputFolder);

            var importerPaths = new List<string>();
            int copies = 0;
            int repointed = 0;
            int undoGroup = Undo.GetCurrentGroup();

            try
            {
                for (int i = 0; i < plan.Meshes.Count; i++)
                {
                    MeshPlan entry = plan.Meshes[i];
                    EditorUtility.DisplayProgressBar(
                        "Generate Mesh LODs",
                        entry.Mesh != null ? entry.Mesh.name : "…",
                        i / (float)Mathf.Max(1, plan.Meshes.Count));

                    if (entry.Action == MeshAction.FlagImporter)
                    {
                        importerPaths.Add(entry.SourcePath);
                        continue;
                    }

                    if (entry.Action != MeshAction.CreateCopy &&
                        entry.Action != MeshAction.RepointOnly)
                    {
                        continue;
                    }

                    Mesh copy = AssetDatabase.LoadAssetAtPath<Mesh>(entry.CopyPath);
                    if (copy == null)
                    {
                        copy = new Mesh();
                        EditorUtility.CopySerialized(entry.Mesh, copy);
                        copy.name = entry.Mesh.name + "_MeshLod";
                        MeshLodUtility.GenerateMeshLods(copy);
                        AssetDatabase.CreateAsset(copy, entry.CopyPath);
                        copies++;
                    }

                    if (!plan.Filters.TryGetValue(entry.Mesh, out List<MeshFilter> filters))
                        continue;

                    foreach (MeshFilter filter in filters)
                    {
                        if (filter == null)
                            continue;
                        if (recordUndo)
                            Undo.RecordObject(filter, "Re-point Mesh Filter");
                        filter.sharedMesh = copy;
                        EditorUtility.SetDirty(filter);
                        repointed++;
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (recordUndo)
            {
                Undo.SetCurrentGroupName("Re-point Mesh Filters");
                Undo.CollapseUndoOperations(undoGroup);
            }

            // Last, and outside the loop: SaveAndReimport is the slowest step
            // and can trigger an asset refresh, so scene work finishes first.
            foreach (string path in importerPaths)
            {
                if (AssetImporter.GetAtPath(path) is ModelImporter importer)
                {
                    importer.generateMeshLods = true;
                    importer.SaveAndReimport();
                }
            }

            AssetDatabase.SaveAssets();
            return new Result(importerPaths.Count, copies, repointed);
        }

        // Creates every missing level of the path, so a hand-typed folder in
        // the window works without the user making it first.
        private static void EnsureFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || AssetDatabase.IsValidFolder(folder))
                return;

            string[] parts = folder.Split('/');
            string built = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = $"{built}/{parts[i]}";
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(built, parts[i]);
                built = next;
            }
        }
    }
}
