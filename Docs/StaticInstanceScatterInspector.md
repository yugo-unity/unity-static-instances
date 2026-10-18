# Static Instance Scatter Inspector

Static Instance Scatter (SIS) renders static MeshRenderer content beneath a component
as instances. Use its Inspector to configure per-instance visibility, mesh LOD
selection and transition fades. Baking and scene operations are in
the [Operations window](OperationsWindow.md); project defaults are in
[Graphics settings](GraphicsSettings.md).

For installation, supported content and a first scene, start with the
[User guide](UserGuide.md). This page describes the component Inspector in its
normal display order. Defaults below apply to a newly added component; saved scenes
and presets retain their authored values.

## Open the Inspector

1. Select the parent GameObject containing your static source renderers.
2. Choose **Add Component > Rendering > Static Instance Scatter**.
3. Select that GameObject to edit the component in the Inspector.

Keep source objects beneath the component and avoid nested SIS components. Instance
transforms, meshes and materials are static after initialization. Rebake and
reinitialize after changing source geometry; moving a source Transform does not
update an already initialized instance buffer.

The Inspector supports multi-object editing. Mixed values are shown as mixed;
per-component bounds and runtime counts require a single selection. Foldout state
is remembered during the Editor session: **LOD** initially opens, while
**Debug (Development)** initially closes.

## Status and setup messages

The top of the Inspector shows read-only bake status and any applicable setup
messages. Multiple messages can appear together.

| Display or action | Purpose |
| --- | --- |
| BatchRendererGroup Variants warning | Appears unless **Project Settings > Graphics > Shader Stripping > BatchRendererGroup Variants** is **Keep All**. Use **Open Project Settings > Graphics ...** to open that page. |
| Bake status | Reports unbaked content, the baked instance count and cache freshness, or blocking validation errors. Multi-selection reports validation status for the selection, with a partial-scan notice for large selections. |
| Nested-component warning | Reports unsupported nested SIS ownership. **Select the nested component** selects the inner component for correction. |
| Baked-prefab warning | Reports world-space baked data stored in a prefab asset or Prefab Mode. **Clear baked data** removes those arrays from the affected prefab components. Bake the placed scene instance instead. |
| LODGroup conversion warning | Existing LODGroups require conversion before SIS can use their content. **Convert LOD Groups in this hierarchy…**, the selected-hierarchies variant, or **Review skipped LOD Groups…** opens a conversion preview scoped to the selection. |

Automatic rebaking depends on **Bake On Entering Play Mode** and **Bake On Build**
in Graphics settings. The status message is not a replacement for checking those
policies. Run **Validate** in the Operations window for the detailed errors.
Conversion and mesh LOD generation are separate operations; see the
[Operations window](OperationsWindow.md) before applying a conversion preview.

## Draw Range and shared fade controls

These rows remain visible when **LOD > Enabled** is off or the level range contains
only one level. They control Draw Range as well as mesh LOD transitions.

| Setting | Purpose | Default and units |
| --- | --- | --- |
| **Draw Range > Min** | Nearest allowed drawing distance. `0` disables the authored near limit. | `0` metres; non-negative. |
| **Draw Range > Max** | Farthest allowed drawing distance. `0` disables the authored far limit; another size-derived cutoff may still apply. | `0` metres; non-negative. |
| **Fade Mode** | Chooses what drives transitions: **None**, **Distance**, or **Time**. Independent of **Transition Metric**. | **Time**. |
| **Fade Width** | Width of a Distance fade band before a mesh LOD boundary and inside an authored Draw Range limit. `0` gives a hard boundary. | `5` metres; non-negative. Editable for Distance fades when crossfade support is active. |
| **Fade Duration** | Duration of a Time transition. `0` follows the project duration; use None for no fade. | `0`; Inspector slider `0–1` seconds. The initial project duration is `0.5` seconds. |
| **Hysteresis** | Additional distance required to switch back across a boundary, reducing repeated switching from camera jitter. Applies to mesh LOD and applicable Draw Range boundaries. | `5` metres; non-negative. Disabled while effective Distance mode has a positive Fade Width. |

Fade Width and Fade Duration stay visible when inapplicable, but are disabled.
Mixed Fade Mode selections keep mode-dependent editing available when project
crossfade support permits it. Changing the mode preserves the other authored values.

### Choose the fade behavior

| Fade Mode | Behavior |
| --- | --- |
| **None** | Hard transitions, with applicable hysteresis. Size-derived limits also hard-cut. |
| **Distance** | Blend follows camera position across a spatial band. Stopping inside the band holds the current blend; reversing direction reverses it. Camera cuts resolve immediately. Fade Width remains in metres even with Screen Size selection. |
| **Time** | A triggered transition progresses over Fade Duration even if the camera stops. Hysteresis controls when a transition is triggered; it serves a different purpose from duration. Shadows follow the visibility fade. |

A positive effective Distance Fade Width supplies the spatial band at that boundary,
so hysteresis does not also move the same transition. Hard boundaries and Time
transition triggers use applicable hysteresis instead. For Draw Range only, the
runtime limits the effective hysteresis to at most half the distance between its
resolved near and far limits. This keeps the two re-entry thresholds from crossing;
it does not impose a half-interval clamp on every mesh LOD boundary.

Fades require the SIS project fade **Enabled** setting and the URP Asset's
**LOD Cross Fade** support. The global development crossfade mask can also disable
them. When support is disabled, effective fades become None while the authored
component mode is retained. Materials must support `LOD_FADE_CROSSFADE`, as URP/Lit
does. A forced mesh LOD disables mesh-to-mesh fades only; Draw Range retains the
shared effective fade mode.

### How Draw Range distances are interpreted

**Draw Range is independent of mesh LOD being enabled.** Its stored Transition
Metric determines how the distances are interpreted:

| Metric | Draw Range interpretation |
| --- | --- |
| **Distance** | Uses each instance's bounding sphere: the authored far limit includes its radius, while the near limit is reduced by its radius, down to zero. Applicable FOV tracking and SIS LOD Bias scale these thresholds. |
| **Screen Size** | Authored Min and Max are absolute camera-to-instance-center distances. They have no radius offset and are not scaled by FOV or SIS LOD Bias. |

Editing Min past a non-zero Max moves Max outward; editing Max below Min moves Min
inward. The edited field takes priority so the Inspector keeps a valid interval.
Max `0` remains an unlimited sentinel.

Out-of-range visibility also controls shadows; Time fades can finish fading out
before emission stops. Min is a radial distance, not the camera near-clip plane.
Perspective SIS culling uses Draw Range instead of the camera's near/far frustum
planes. Author an explicit Max when you require a distance cap, including for scenes
that extend beyond the camera's far clip distance.

### Additional far-cull sources

| Source | Interaction with Draw Range and fades |
| --- | --- |
| Screen Size **Culled (% Screen Size)** | Sets a size-proportional far cutoff for this component. Draw Range Max remains an additional absolute cap; whichever cutoff is reached first wins. |
| Distance metric plus project **Size-Based Culling > Cull Distance Scale** | Derives a limit from each instance's baked sphere size and shape correction. Project scale `0` disables this source. With an authored Max, the smaller resulting threshold wins. |

A size-derived winning limit uses the project's **Derived Cull Fade Fraction** for
its Distance-mode band, including a winning Screen Size Culled ratio. The project
size-cull scale being inactive in Screen Size does not disable this fade fraction.
An authored winning limit uses Fade Width. Equal far limits use the authored
width in Distance selection; Screen Size selection retains the ratio-derived width
at equality and switches to the authored width only when the absolute Max is
strictly smaller. Time uses Fade Duration, and None hard-cuts. The near side uses
the authored fade controls.

The project size-cull scale does not apply to Screen Size components, even with
mesh LOD disabled. Their saved Culled ratio and absolute Draw Range still apply.
The metric and ratio editing controls are inside the active multi-level LOD section;
disabling that section does not reset these saved range settings. To inspect or
change them in this Inspector, temporarily enable a multi-level LOD range, then
restore the intended LOD Enabled state.

## Motion Vector

This option sits directly below **Hysteresis**, before Bounds, and defaults **off**. Enable
it before initialization for custom vertex animation (for example VAT) when the
material provides the corresponding URP MotionVectors pass. It is
an initialization setting; changing it requires reinitialization.

For VAT/custom deformation the shader must supply correct previous deformed vertex
positions and support DOTS instancing. Enabling this option requests a pass; it
does not construct animation history or enable TAA/motion blur. Ordinary immutable
instance transforms are still required. See [the shader requirements](UserGuide.md#motion-vectors-for-vertex-animation).

Groups that draw a wind-stripped SpeedTree clone never raise per-object MV. The
option remains usable for other groups in that owner.

## Bounds and spread measurements

These values describe baked content and are read-only. They do not provide an
editable culling box or replace per-instance bounds.

| Display | Purpose |
| --- | --- |
| **Bounds Center** | World-space center of the union AABB containing all baked instances. |
| **Bounds Size** | World-space dimensions of that union AABB, in metres. Unexpectedly large dimensions can reveal a stray instance. |
| **Spread Diagonal** | Union AABB diagonal, in metres. |
| **Mean Instance Size** | Mean instance bounding-sphere diameter, in metres, plus the ratio of spread diagonal to this mean diameter. |

An unbaked component shows **Bounds: bake to display**. Multi-selection shows the
number of selected scatters instead. The two spread rows appear only for a single
baked component with a positive spread ratio at or below `50x`.

## LOD

Enable mesh LOD after the source meshes contain the required mesh LOD levels.
**Screen Size** with **Time** fades is the recommended starting configuration.
Changing Transition Metric does not generate missing mesh levels.

| Setting | Purpose | Default and availability |
| --- | --- | --- |
| **Enabled** | Enables the component's mesh LOD selection. Off draws mesh level 0 while Draw Range and its fades remain active. | Off. |
| **LOD Level Range** | Min/Max mesh levels used by the ladder. Level 0 is the finest. Sliding the range changes the level labels while retaining authored boundary values. | Min `0`, Max `1`; authorable levels `0–3`, Max at least `1`. Visible when Enabled; locked in Play Mode. |
| **Transition Metric** | Selects **Screen Size** percentages or **Distance** metres for mesh LOD selection. | Screen Size. Visible when Enabled and Max is greater than Min. |
| **Reference FOV** | Reference vertical field of view for authored Distance boundaries when project FOV Tracking is enabled. | `1–179` degrees; seeded from a perspective Camera.main when added/reset, otherwise `50`. Visible for Distance or a mixed metric selection. |
| **Shadow Cast Max Level** | Stops shadow casting after the boundary ending the selected level, while camera visibility continues. | **Every level casts** (`-1`). Visible with an active multi-level range. |

A single-level range has no transitions. An incomplete ladder remains at its finest
authored level until the missing boundaries are supplied. Increasing the level
range or using **Author the missing N boundaries** supplies missing entries.
Shrinking the range retains existing boundary values.

### Screen Size ladder

The **Ratios** bar decreases from full screen coverage on the left toward zero on
the right. Drag a level boundary or the Culled edge to edit it. Numeric sliders
below the bar provide the same authoring controls.

| Setting | Purpose | Units and initial authoring values |
| --- | --- | --- |
| **LOD n Transition (% Screen Size)** | Screen-height coverage of the instance's bounding sphere when leaving LOD n for the next level. Boundaries descend and cannot cross their neighbours. | Percent; `0.01–100`, further constrained by adjacent boundaries. Newly supplied entries start at `50%`, then halve: `25%`, `12.5%`. |
| **Culled (% Screen Size)** | Coverage below which the instance stops drawing. The Culled region occupies the right end of the bar. | `0–100%`, constrained below the last transition; default `0` disables ratio culling. |

API ratio values are fractions: `0.5` corresponds to `50%` in the Inspector.
Perspective FOV is incorporated automatically, independently of the project
FOV Tracking toggle. Orthographic/no-usable-FOV evaluation uses the stored Reference
FOV fallback; it does not model orthographic size. Reference FOV is hidden in this
metric; switch to Distance to view or edit it, then switch back.

Authored Draw Range Min/Max remain active but are not plotted on the ratio axis.
An explanatory message appears when either is authored. Fade Width and Hysteresis
also remain in metres and are not drawn as ratio bands.

### Distance ladder

The **Distances** bar increases from the camera on the left toward the coarsest
level on the right. Drag a boundary or edit its numeric field.

| Setting | Purpose | Units and initial authoring values |
| --- | --- | --- |
| **LOD n Transition (m)** | Camera distance to the surface of each instance's AABB when leaving LOD n. Boundaries ascend and are constrained by neighbours and an authored far-cull limit. | Metres. Newly supplied entries start at `100 m`, then double: `200 m`, `400 m`. |
| **Culled** bar regions | Visualize the authored Draw Range limits on the distance axis. | Edit the Draw Range row above. |

Distance and Screen Size ladders are stored separately. Switching metrics restores
that metric's authored values rather than converting the other ladder. Distance
selection applies no FOV compensation to orthographic/no-usable-FOV views; the
stored Reference FOV fallback described above belongs to Screen Size selection. Project
**LOD > Distance Scale**, **LOD Bias**, **Min Level** and optional **FOV Tracking**
affect evaluation without rewriting the authored ladder; see
[Graphics settings](GraphicsSettings.md).

On both bars, greyed LOD segments indicate levels lifted to a coarser mesh by the
project Min Level. Mixed ladders show a placeholder; select one component to edit
its individual boundaries. A warning can report overlapping Distance fade bands;
it does not automatically reduce Fade Width.

### Shadow cutoff and missing levels

**Shadow Cast Max Level** offers each level except the coarsest, because ending
shadow casting after the coarsest level would change nothing. Values at or above
the coarsest display **Every level casts**; values below the window minimum are
raised to that minimum. The cutoff follows the owner's fade/transition behavior.
With no active multi-level ladder there is no level boundary for this cutoff.

When a source mesh lacks the requested level, it uses the coarsest level it has.
For a single selection with active LOD, an information message reports affected
mesh and instance counts. Expand **Meshes without the level** for the read-only
list. Generate the required mesh levels through the model importer or the
[Operations window](OperationsWindow.md).

## SpeedTree content

The Inspector has no SpeedTree wind controls, and SIS does not simulate wind. For
baked draw groups whose material enables a SpeedTree wind keyword, the explicit bake
operations in the [Operations window](OperationsWindow.md) generate a wind-stripped
clone material, and SIS draws that clone so the trees render still with no wind
vertex cost. A group with no generated clone keeps its original material and still
renders still, but keeps paying the wind vertex cost; re-bake to generate the clone.

## Debug (Development)

Use these controls to inspect behavior or compare rendering configurations. The
first group belongs to this component; **Global (all SIS)** affects every SIS.

| Component setting or display | Purpose | Default and conditions |
| --- | --- | --- |
| **Forced LOD** | Pins mesh LOD to a level within the authored window. Auto restores automatic selection. Draw Range fades remain active. | **Auto** (`-1`). |
| **Effective LOD Fade** | Read-only effective mesh transition mode after forced LOD and global fade gates. | Derived; forced mesh LOD makes it None. |
| **LOD Offset** | Shifts the selected level; positive is coarser, negative finer. The result stays within the component's level range. | `0`; slider span follows the window depth, with a minimum span of `-1–1`. |
| **Visible** | Suppresses this component's camera and shadow output when off while retaining its rendering resources. | On. |
| **Cast Shadows** | Master mask for this component's shadow casting. When on, source renderers' ShadowCastingMode is still respected. | On. |
| **Instances** | Read-only initialized instance count. | Play Mode, single selection. |
| **Draw Groups** | Read-only initialized instanced draw-group count, grouped by mesh, submesh, material and renderer attributes. | Play Mode, single selection. |
| **Unique Meshes** | Read-only registered mesh count. | Play Mode, single selection. |
| **Unique Materials** | Read-only registered material count. | Play Mode, single selection. |

Runtime counts describe initialized content; they are not a per-frame visible
instance or draw-call counter. Outside Play Mode, a message replaces the counts.
Multi-selection in Play asks you to select one scatter.

| Global (all SIS) setting | Purpose | Default and conditions |
| --- | --- | --- |
| **Cross Fade Enabled** | Development mask for mesh LOD and Draw Range fades across all SIS. | Initially on; effective behavior also requires project fade settings. Play Mode only, disabled when URP LOD Cross Fade is off. |
| **Forced LOD (Global)** | Forces every SIS to a mesh LOD, clamped to each component's supported window. Auto leaves component selection in control. | **Auto**; Play Mode only. |

Global masks are temporary and are not saved with a component. Their defaults are
restored when the scripting domain resets; the Inspector warns about reset on
script recompile and exiting Play Mode. Do not rely on them as saved settings when
using Play Mode options that disable domain reload. Use [Graphics settings](GraphicsSettings.md) for
saved project settings. Changing a forced LOD preserves the authored Fade Mode;
return to Auto to restore automatic mesh transitions.

## Related documentation

- [User guide](UserGuide.md): scene adoption and supported workflows.
- [Graphics settings](GraphicsSettings.md): project-wide quality, fade and build policies.
- [Operations window](OperationsWindow.md): validation, scene baking, conversion and mesh LOD generation.