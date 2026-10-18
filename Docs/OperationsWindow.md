# Operations window

Use the Static Instances window to bake scenes, convert existing LODGroup content,
generate mesh LODs, and generate wind-stripped SpeedTree materials. Component
controls belong in the [SIS Inspector](StaticInstanceScatterInspector.md); project policies belong in
[Graphics Settings](GraphicsSettings.md).

## Open the window

Choose **Window > Rendering > Static Instances**. Alternatively, open **Edit >
Project Settings > Graphics > Static Instances** and select **Open Operations
Window**. The window title is **Static Instances**.

Start scene operations in Edit mode. The main actions are disabled during Play,
Play transitions, or an active scene batch. A valid active scene is required.
An empty target set normally reports that there is nothing to do; it does not
necessarily disable a button. Read operation results and validation messages in
the Console, and batch progress in the window's status area.

## Scene Operations

The **active scene** is the scene selected as active in the Hierarchy. **Loaded
scenes** includes additively loaded scenes. The buttons deliberately have different
scopes; selecting an object does not restrict their scope.

| Setting | Purpose |
| --- | --- |
| **Bake Loaded Sources** | Bakes SIS sources in all loaded scenes. This action saves only the active scene; save other modified scenes separately. |
| **Validate** | Reports bake/source issues for targets across loaded scenes without changing them. Resolve blocking source errors before baking. |
| **Clear Baked Data** | After confirmation, removes SIS bakes in the active scene and saves it. Other loaded scenes are untouched. It then collects wind-stripped material clones no longer referenced elsewhere. |
| **Convert FOV** | Vertical field of view used for the one-time conversion of LODGroup screen heights to metric distances. Range: 10–120 degrees. Initially uses Camera.main's FOV, or 60 degrees if absent. No reference resolution is required. This input is not saved or tracked after conversion. |
| **Convert LOD Groups…** | Opens a preview for LODGroups in the active scene. Applying conversion retains LOD0 and removes coarser renderers and LODGroups. Review the hierarchy changes before applying. |
| **Generate Mesh LODs…** | Opens a preview for meshes under SIS components across loaded scenes. Enables supported importer generation or creates mesh-copy assets and repoints MeshFilters. This is a separate step after LODGroup conversion. |

**Clear Baked Data** records component Undo, but deleted material assets cannot be
recovered with Undo. Referenced clones are retained. If the scene save fails, the
clear remains in memory and no material collection runs.

### Convert LOD Groups preview

The preview displays the scene scope, planned changes, converted metric distances
and warnings before changing the hierarchy. The conversion incorporates the
current Unity `QualitySettings.lodBias`; the resulting distances do not track later
changes to that value. Conversion does not author a Screen Size ratio ladder.

| Setting | Purpose |
| --- | --- |
| **Restore "LOD1+ do not cast" as Shadow Cast Max Level** | Optionally derives a shadow-level cutoff for each qualifying SIS owner from that owner's groups. Review partial-majority warnings: the cutoff also affects its nonqualifying groups. A stored cutoff has no effect without a live LOD ladder. |
| Skipped-group checkboxes | Opt individual groups into conversion despite detected dynamic evidence. Leave genuinely animated, moving or simulated content outside SIS. A skipped LODGroup under an owner still prevents its bake. |
| **Only objects with custom components** | Filters the deletion list. Turn it off to inspect every GameObject scheduled for deletion, including objects driven by another object's script. |
| Search | Filters the deletion list by object name. Filtering does not protect hidden objects. |
| Deletion-list checkboxes | Protect selected GameObjects from deletion. Their coarse renderers are still removed. Use this for required gameplay objects that automatic detection cannot identify. |
| **Select** in a group or deletion row | Selects and pings that GameObject for inspection; it does not change the conversion plan. |
| **Cancel** | Closes the preview without applying conversion. |
| **Convert** | Applies the reviewed plan and leaves the scene dirty. Disabled when no groups will convert. Save afterward. |

Scene conversion normally records Undo. For more than 2,000 affected renderers,
the tool offers conversion with or without Undo because recording can be expensive.
Neither choice reverses the separate importer or generated-asset stage.

### Generate Mesh LODs preview

The preview shows the loaded-scene scope and counts for importer changes, generated
copies, reference updates, existing chains, previously processed single-level meshes,
and meshes without asset paths. A mesh that remains single-level after generation
can simply have too few triangles to decimate; it still renders at its only level.

| Setting | Purpose |
| --- | --- |
| **Copy asset folder** | Destination for generated mesh copies, created if needed. Changing the folder reanalyzes the plan. Copies in another folder are not found by this lookup and can be generated again. |
| **Also list the meshes nothing happens to** | Includes unchanged meshes in the preview. With it off, the list shows this run's changes. |
| Mesh list | Shows the planned action and source for each listed mesh. Review it before generating. |
| **Select** in a mesh row | Selects and pings the source mesh asset for inspection. |
| **Cancel** | Closes the preview without generation. |
| **Generate** | Applies the plan. Disabled when there is no work. Save scenes whose MeshFilters were repointed. |

Importer changes and generated mesh assets are not undoable. Use version control
or an asset backup before applying them. MeshFilter reference changes normally
record Undo; above 2,000 repoints the dialog offers generation with or without that
recording. This choice does not make asset changes undoable. Inspect the resulting
mesh chains and author the desired LOD settings in the SIS Inspector.

## Selected Scenes

Use this section to bake and save multiple project scenes sequentially.
The list is sorted by asset path. Package scenes are shown as read-only entries;
only scene paths under **Assets/** can be selected.

| Setting | Purpose |
| --- | --- |
| **Select All** | Checks all listed project scenes under Assets. |
| **Select None** | Clears the selection. |
| **Refresh** | Refreshes the scene list. Removed paths are removed from the selection; project changes also refresh the list. |
| Scene checkboxes | Select the saved scenes to process. Package rows cannot be checked. |
| **Bake Selected Scenes** | Validates and bakes each selected scene, generates wind-stripped SpeedTree materials, and checks that scene's save before advancing. It does not enter Play mode. Requires at least one selected scene. |
| **Cancel After Current Operation** | Requests cancellation of a running batch. Previously completed saves remain saved. |
| Status area | Reports progress, completion, cancellation, and errors, including save or scene-restoration failures. |

Before traversal, save the open scenes when prompted. Cancel stops startup. Choosing
**Don't Save** does not authorize discarding edits: any scene still dirty or without
a saved path prevents traversal. Each selected scene is validated before its owners
are changed. Stale or relocated bake data that a rebake can repair does not itself
block that rebake; blocking source errors do.

On successful batch completion, the original loaded-scene setup is restored. If an
operation leaves unsaved changes, including after a failed save, the tool keeps that
scene open instead of discarding the edits to restore the previous setup.

Batch saves, scene traversal and asset creation/deletion do not form one atomic
Undo transaction. Individual bake and record updates record component Undo, but do
not rely on Undo to roll back a completed multi-scene batch. Keep a saved baseline
or version-control checkpoint for that purpose.

## Generated assets

SIS does not simulate SpeedTree wind. For every baked draw group whose material
enables a SpeedTree wind keyword (`_WIND_SHARED`, `_WIND_BRANCH1`, `_WIND_BRANCH2`,
`_WIND_RIPPLE`, `_WIND_SHIMMER`), **Bake Loaded Sources** and **Bake Selected Scenes**
generate a wind-stripped clone material, and SIS draws that clone so SpeedTree trees
render still with no wind vertex cost. Build-time automatic bakes re-attach existing,
up-to-date clones but cannot create new ones. A group with no generated clone keeps its
original material and still renders still, but keeps paying the wind vertex cost.

| Asset or state | Handling |
| --- | --- |
| Wind-stripped materials | Persistent generated assets under `Assets/SIS_WindStatic`. SIS draws them instead of the original SpeedTree materials. Clear collects only eligible unreferenced clones after a successful scene save. |
| Mesh LOD copies | Persistent assets in the preview's Copy asset folder. They are separate from wind-stripped materials and are not removed by undoing scene reference changes. |

## Related pages

- [User guide](UserGuide.md): scene adoption and runtime overview.
- [SIS Inspector](StaticInstanceScatterInspector.md): per-component authoring controls.
- [Graphics Settings](GraphicsSettings.md): project-wide settings and automatic bake/build policies.
