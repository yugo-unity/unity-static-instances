# Static Instances user guide

Use this guide to adopt Static Instance Scatter (SIS) in a scene, choose a starting configuration and find
runtime controls. For supported Unity/package versions and installation, see the
[README](../README.md#requirements).

## GUI reference

Detailed settings are organized by the window in which you edit them:

| Page | Use it to |
| --- | --- |
| [Static Instance Scatter Inspector](StaticInstanceScatterInspector.md) | Configure one SIS owner's visibility, shadows, mesh LOD, Draw Range and fades. |
| [Static Instances Graphics Settings](GraphicsSettings.md) | Configure project-wide quality, automatic baking and stripping policies. |
| [Static Instances operations window](OperationsWindow.md) | Validate, bake, convert LODGroups and process saved scenes. |

## Adopt a scene

1. Use opaque, BRG/DOTS-instancing-compatible MeshRenderers. Place the static source
   objects below a parent with **Rendering > Static Instance Scatter**. Keep each
   MeshRenderer's **Batching Static** flag off. Do not nest SIS owners.
2. Save the scene. Open **Window > Rendering > Static Instances**, or use
   **Open Operations Window** in **Project Settings > Graphics > Static Instances**.
3. Run **Validate** and resolve reported unsupported content before baking.
4. Select the saved scene's row and run **Bake Selected Scenes**.
   The operation validates, bakes, generates wind-stripped SpeedTree
   materials and saves each selected scene in sequence.
5. Enter Play and inspect the result. SIS suppresses the ordinary source renderers
   while it owns the content; retain the source hierarchy for authoring.

Instance transforms, meshes, materials and rendering attributes are static after
initialization. Moving or replacing source geometry requires rebaking and
reinitialization. Editing a Transform does not update an initialized instance buffer.
Visibility, shadow and LOD controls have runtime APIs described below.

### Component lifetime in Players

Keep source scenes for authoring. **Bake On Build** rebakes stale content on the
build's scene copies; with it off, rebake before building.

In Players where the build processor stripped this owner's sources, initialization
uploads prepared matrices and releases their CPU storage. Disabling the component
or its GameObject stops SIS rendering while preserving GPU and culling resources;
enabling it again reuses them. Destroy/unload releases those resources. Surviving
script-bearing source renderers remain suppressed. Editor and builds that retain
sources use the disable/dispose/source-restore lifecycle.

`GetBakedData` reconstructs a fresh Matrix4x4 array on each explicit query and
returns false after a stripped Player releases matrix storage. Use
`BakedInstanceCount` and `GetBakedDrawGroups()` for metadata without decoding.

### Existing LODGroup content

SIS does not render LODGroups directly. Use **Convert LOD Groups…**, then
**Generate Mesh LODs…**, and review both previews before applying them. Choosing a
LOD metric alone does not create mesh levels. See the
[operations window reference](OperationsWindow.md) for the conversion's scope,
asset changes and Undo limits.

## Choose LOD selection and fades separately

LOD is **disabled by default**. When meshes have the required mesh LOD levels, enable
it and start with **Screen Size** selection and **Time** fades.

- Choose **Screen Size** to allocate detail according to projected size, or
  **Distance** to author transition boundaries in metres.
- Choose **Time** for a transition that completes over time, **Distance** for a
  blend that follows camera position, or **None** for a hard switch.

These are separate choices. Fade support also needs project **Fade > Enabled** and
the URP Asset's **LOD Cross Fade**. Read the
[SIS Inspector reference](StaticInstanceScatterInspector.md) for units, ladders,
forced LOD and effective modes; read [Graphics Settings](GraphicsSettings.md) for
project quality overrides.

## Visibility, Draw Range and Size-Based Culling

**Visible** controls the owner's rendering, including shadows. **Cast Shadows**
can disable only its shadows while leaving camera rendering enabled. **Draw Range**
works even when mesh LOD is disabled. Start with an explicit Max if you need a distance cap;
0 means no authored far limit. Distance and Screen Size use different distance
conventions, so consult the [SIS Inspector](StaticInstanceScatterInspector.md) before
authoring precise boundaries.

For size-based removal, use a Screen Size owner's **Culled** threshold, or the
project's **Cull Distance Scale** for Distance-mode owners. Their detailed behavior
and interactions with Draw Range are in [Graphics Settings](GraphicsSettings.md).

## Bake, build and strip

For routine authoring, use **Bake Selected Scenes** to process and save
saved project scenes. **Bake Loaded Sources** instead bakes loaded scenes but saves
only the active one; save other changed scenes yourself. The
[operations window reference](OperationsWindow.md) explains all command targets,
cancellation and save/Undo behavior.

Automatic Play baking and build baking default to enabled. Play baking can leave
an authoring scene dirty; build baking works on the pipeline's scene copy.
Keep **BatchRendererGroup Variants = Keep All** and review source stripping before
shipping. For defaults, shader inclusion and optional Addressables setup, read
[Graphics Settings](GraphicsSettings.md).

## Runtime API essentials

Use `StaticInstances`. Configure the mesh level range and source hierarchy before
initialization; `OnEnable` initializes the component. These APIs do not provide
runtime instance addition, removal, mesh replacement or transform editing.

| API | Intended use |
| --- | --- |
| `StaticInstanceScatter.Visible`, `CastShadows`, `ShadowCastMaxLevel` | Control owner visibility (including shadows), or disable/limit only its shadows. |
| `MinDrawDistance`, `CullDistance` | Update authored near/far bounds. Values are nonnegative; use 0 for the absent limit and keep Min/Max coherent. |
| `LodEnabled`, `LodMode`, `MinMeshLodLevel` | Enable/select an already authored ladder or change its minimum level. `MaxMeshLodLevel` is an authoring-time depth setting. |
| `SetLodRatios(float[])`, `SetLodDistances(float[])` | Supply complete ladders in fractions or metres. Match the authored transition count and ordering. Arrays are accepted as supplied; do not mutate accessor-returned arrays and assume a live update. |
| `FadeMode`, `CrossFadeWidth`, `LodFadeDuration` | Update fade behavior. A duration of 0 follows the project default. |
| `ForcedLodLevel`, `LodLevelOffset` | Force a level (`-1` = Auto) or offset automatic selection. The force does not disable Draw Range fades. |
| `IsInitialized`, `InstanceCount`, `DrawGroupCount`, `HasBakedData` | Inspect initialization/bake state; a serialized bake is not a guarantee of current source freshness. |
| `StaticInstanceLodSystem.LodBias`, `LodDistanceScale`, `MinMeshLodLevel`, `CrossFadeEnabled` | Runtime global overrides. Project settings are applied on startup; `StaticInstanceGlobalSettings.ApplyToSystem()` restores their authored values. |

`StaticInstanceLodSystem.RegisterDrivingCamera(camera)` registers a gameplay camera;
check its bool result. Up to four driving cameras are supported. With no explicit
registrations, Camera.main is the fallback. Use `UnregisterDrivingCamera` when an
explicit camera is removed, or `ClearDrivingCameras` to return to the fallback.
Unregistered views can render using another camera's LOD decisions; register each
camera that needs independent decisions.

After a camera cut, call `StaticInstanceLodSystem.SnapFades(camera)` on the main
thread. True means the request was accepted. The next eligible evaluation completes
that camera's Time LOD and Draw Range fades against its new position; already
submitted views are preserved. It does not synchronously finish rendering or affect
other cameras and Distance/None fades.

Editor automation can use the public `StaticInstanceBakeUtility` bake/validate/clear
operations. Internal batch classes are not a public automation contract.

## Motion vectors for vertex animation

Enable **Motion Vector** below **Hysteresis** in the SIS Inspector before initialization
when a shader animates vertices and needs object motion for TAA or motion blur.
The option defaults off. Changing
it requires reinitialization; it does not update an already registered batch live.

The material must use a BRG/DOTS-instancing-compatible URP shader with a
`MotionVectors` LightMode pass. Its vertex animation (for example VAT or procedural
deformation) must supply both the current and previous deformed positions. SIS
provides the fixed per-instance transforms, but does not generate a previous VAT
frame, cache arbitrary shader deformation or create a missing shader pass.
A standard undeformed pass cannot describe custom animated vertices correctly.
Use mesh bounds that enclose the animation before baking, because SIS culling
uses baked bounds. Instance Transform/mesh/material references remain immutable.

The enabled option adds motion-vector draw work when the renderer requests that
pass. It does not itself enable TAA or motion blur. A static object still has
camera motion when the camera moves; per-object vertex-motion output is a
separate contribution. Groups that draw a wind-stripped SpeedTree clone never
raise the extra per-object pass; this does not affect another group's custom
vertex-animation motion vectors.

SIS does not provide an animation playback system or arbitrary per-instance
material-property history. Verify the material's own previous-frame inputs and
actual Player output for your animation before use.

## SpeedTree content

SIS does not simulate SpeedTree wind, and there is no setting for it. For every baked
draw group whose material enables a SpeedTree wind keyword (`_WIND_SHARED`,
`_WIND_BRANCH1`, `_WIND_BRANCH2`, `_WIND_RIPPLE`, `_WIND_SHIMMER`), the explicit bake
operations (**Bake Loaded Sources** and **Bake Selected Scenes**) generate a
wind-stripped clone material under `Assets/SIS_WindStatic/`, and SIS draws that clone
instead, so SpeedTree trees render still with no wind vertex cost. Build-time
automatic bakes re-attach existing, up-to-date clones but cannot create new ones.

A group with no generated clone keeps its original material and still renders still
(SIS supplies a zeroed wind record), but keeps paying the wind vertex cost. An info
log at initialization names the re-bake as the remedy. **Clear Baked Data** deletes
clones that are no longer referenced.

## Current limitations and checks before shipping

- No hierarchical LOD (HLOD), nested SIS ownership, skinned/transparent rendering,
  lightmap support, direct LODGroup rendering, SIS-managed per-instance material
  animation, or supported simultaneous SIS and GPU Resident Drawer (GRD) rendering.
- Per-renderer MaterialPropertyBlock variations are not preserved. Validate reports
  this; a shared material does not reproduce those per-object values automatically.
- World-space scene bakes are not freely relocatable prefab data. Runtime source
  collection is a development fallback, not the same loading-cost path as a baked scene.
- Check saved-scene bakes, shader inclusion, source stripping, cameras and the actual
  Player before shipping. The validated environment is Windows D3D12 with the
  versions in the README. Profile with your own content; no general performance
  advantage over GRD is claimed.
