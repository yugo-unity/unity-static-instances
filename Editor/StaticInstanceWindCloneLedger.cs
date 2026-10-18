using System.Collections.Generic;
using UnityEngine;

namespace StaticInstances.Editor
{
    /// <summary>
    /// The clone GC's ledger, as a ScriptableObject asset
    /// inside <c>Assets/SIS_WindStatic/</c> — beside the clones it describes.
    ///
    /// **Why it is an asset and not a `UserSettings/` JSON file.**
    /// The registry describes COMMITTED content. A fresh `git clone` has no
    /// `UserSettings/`, so every clone a repository carries would be UNKNOWN on
    /// every new machine — and unknown means "keep for ever" by the GC's own safety rule,
    /// which makes the collector dead for every new team member. The record has to travel
    /// with the thing it records: the ledger and the clones are committed (or ignored)
    /// together, so the ledger always shares its clones' fate, which is the
    /// property that matters.
    ///
    /// A ScriptableObject rather than a JSON file under `Assets/`: once the file lives in
    /// the asset folder, JSON's one advantage — staying out of the AssetDatabase — is gone,
    /// and the SO gives an Inspector view so rows can be read and pruned by hand.
    ///
    /// **STRINGS ONLY, and this is a correctness condition rather than a style choice.**
    /// A row may never hold a `Material` reference. `AssetDatabase.GetDependencies` is
    /// safeguard 2, and an object reference here would make the ledger itself a referrer of
    /// every clone it lists — so the veto would report the bookkeeping as a reason to keep
    /// the thing the bookkeeping exists to collect, and nothing would ever be deleted.
    /// </summary>
    internal sealed class StaticInstanceWindCloneLedger : ScriptableObject
    {
        /// <summary>One generated clone, with the display name the filename does not carry.</summary>
        [System.Serializable]
        internal sealed class CloneEntry
        {
            /// <summary>Asset path. A STRING — see the type's remarks on why never a reference.</summary>
            public string Path;

            /// <summary>
            /// The source material's name, for humans reading this asset. It is
            /// not part of the clone's filename (it is not part of the
            /// identity, and would make the path unstable under a rename), so the
            /// readable half lives here.
            /// </summary>
            public string SourceName;
        }

        [System.Serializable]
        internal sealed class SceneRow
        {
            public string SceneGuid;

            /// <summary>
            /// Stored for humans, never matched on: a row whose scene was deleted is
            /// accepted rather than chased, and this is what lets someone recognise it.
            /// </summary>
            public string SceneName;

            public List<CloneEntry> Clones = new List<CloneEntry>();
        }

        /// <summary>Rows, kept sorted by scene GUID — see <c>Sort</c>.</summary>
        public List<SceneRow> Rows = new List<SceneRow>();

        /// <summary>
        /// Clones the GC knows about that NO scene claims any more — kept because
        /// something outside the ledger still references them, or because the delete was
        /// refused. Separate from the rows on purpose: an orphan must not read as "a scene
        /// needs this" (that would block another scene's collection), but it must not read
        /// as "unknown" either, because unknown means keep for ever. Every Clear
        /// reconsiders them.
        /// </summary>
        public List<CloneEntry> Orphans = new List<CloneEntry>();

        /// <summary>
        /// Sort rows by scene GUID before writing. The ledger is a committed file in an
        /// adopter project, so a stable order is what keeps two people's presses from
        /// producing a merge conflict out of nothing but ordering.
        /// </summary>
        internal void Sort()
        {
            Rows.Sort((a, b) => string.CompareOrdinal(a.SceneGuid, b.SceneGuid));
            for (int i = 0; i < Rows.Count; i++)
                Rows[i].Clones.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
            Orphans.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        }
    }
}
