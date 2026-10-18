# Static Instances Graphics Settings

Use these settings to configure project-wide SIS quality and build policies.
For a single owner's visibility, LOD ladder and Draw Range, use the
[Static Instance Scatter Inspector](StaticInstanceScatterInspector.md). For a first
scene, start with the [user guide](UserGuide.md).

## Open the settings

Open **Edit > Project Settings > Graphics**, then find **Static Instances** in the
URP settings. The quality sections appear first, followed by the operations-window
button, bake policies and stripping policies.

Defaults below describe newly created settings. Existing settings retain their
saved values. Most quality settings apply when edited in Play Mode; **Cull Distance Scale**
rebuilds culling data. These are not interchangeable per-frame animation controls.

## LOD

| Setting | Purpose |
| --- | --- |
| **Min Level** | Project-wide minimum mesh LOD level, from **0** to **3**; default **0**. Higher levels are coarser. It combines with each owner's minimum so the coarser minimum wins within the available range. It does not generate mesh levels or rewrite transition distances. |
| **Distance Scale** | Multiplies supported LOD distances. Default **1**; Inspector range **0.25–2**. Below 1 switches to coarser detail sooner; above 1 retains detail farther away. |
| **LOD Bias** | Additional SI-only multiplier on LOD and scalable Draw Range distances. Default **1**; Inspector range **0.25–2**. It composes with Distance Scale and is independent of Unity's `QualitySettings.lodBias`. |

Screen Size selection still uses its authored ratios and camera FOV. Its absolute
Draw Range Min/Max bounds remain absolute despite these multipliers. Distance-mode
scalable limits include SI's LOD Bias. See the Inspector's
[Draw Range](StaticInstanceScatterInspector.md#how-draw-range-distances-are-interpreted) reference before choosing a distance
cap; changing project quality does not make every type of boundary scale identically.

## Fade

| Setting | Purpose |
| --- | --- |
| **Enabled** | Master switch for SIS dither fading; default **on**. Turning it off makes mesh LOD and Draw Range transitions hard switches, while retaining each owner's authored Fade Mode. Turning it on permits those authored modes; it does not override an owner's **None** mode. |
| **Duration** | Default duration in seconds for **Time** fades; default **0.5**, Inspector range **0–1**. Used when an owner's **Fade Duration** is 0. A nonzero owner duration overrides it. |
| **Curve** | Shape of both time-driven and distance-driven dither fades. **Linear** is the default; **Sine Ease** softens the ends. It changes the blend shape, not the selected level or cull boundary. |

> [!NOTE]
> The active URP Asset must also enable **LOD Cross Fade**. Otherwise effective
> fading is disabled even when this setting is on. Forcing an owner's LOD disables
> its mesh-LOD fade, but does not independently disable its Draw Range fade.

## Size-Based Culling

| Setting | Purpose |
| --- | --- |
| **Cull Distance Scale** | Distance per unit of baked instance size. Default **0** disables this source of far culling; Inspector range **0–100**. Distance-mode owners derive a far threshold from each instance's bounding-sphere diameter and shape correction. A smaller derived threshold can cull before an authored Draw Range Max. Changing the scale rebuilds culling data; use it for authoring or infrequent quality changes. |
| **Derived Cull Fade Fraction** | Spatial fade-band fraction when a size-derived far limit (including a Screen Size **Culled** ratio) governs a **Distance** fade. Default **0.15**; Inspector range **0–0.5**. For a resolved 700 m limit, 0.15 gives a band of about 105 m. Zero removes this spatial band. Time fades use duration instead; None hard-cuts. |

**Cull Distance Scale** does not affect owners using **Screen Size**, even when
their mesh LOD is disabled. Use the owner's **Culled** ratio or explicit Draw Range
Max instead. **Derived Cull Fade Fraction** still supplies the Distance fade band
when a Screen Size ratio-derived far limit wins.
When authored and derived Distance-mode limits coexist, the smaller effective
threshold wins. At equality, the authored limit supplies the fade width. An authored
near boundary uses the owner's Fade Width, not Derived Cull Fade Fraction.

Tune size-based culling against the rendered result, not the number of rejected
instances. Thin or flat objects can have a small corrected size while still forming
important silhouettes or visible structure, so aggressive settings can remove
surfaces such as paving, foliage sheets or flags too early. Inspect representative
views and distances and judge the affected pixels and silhouettes before adopting
a project-wide scale.

## Camera

| Setting | Purpose |
| --- | --- |
| **FOV Tracking** | Optional FOV compensation for **Distance**-mode boundaries; default **off**. Uses each owner's **Reference FOV**. Narrowing a perspective camera's FOV extends scalable boundaries, including its authored far range. Screen Size incorporates camera FOV independently of this switch. |

The owner's reference is an authored value, not a live link to Camera.main.
Distance mode applies no FOV compensation to orthographic or unusable-FOV views.
Screen Size mode instead falls back to the stored reference FOV for those views;
it is not a model based on orthographic camera size. A zoom can retain more geometry with
FOV Tracking enabled, so assess the quality/cost tradeoff with your content.

## Shadows

| Setting | Purpose |
| --- | --- |
| **Receiver Sphere Cull** | Default **on**. Rejects shadow casters that cannot reach a shadow receiver, at cluster and individual-instance levels. This is independent of an owner's **Cast Shadows** and **Shadow Cast Max Level**. Its usefulness depends on the view and receiver coverage; it is not a guaranteed speedup for every scene. |

## SpeedTree content

There is no SpeedTree wind setting. SIS does not simulate wind. For baked draw
groups whose material enables a SpeedTree wind keyword, the explicit bake operations
in the [operations window](OperationsWindow.md) generate a wind-stripped clone
material and SIS draws that clone, so SpeedTree trees render still without wind
vertex cost. A group with no generated clone keeps its original material and still
renders still, but keeps paying the wind vertex cost.

## Operations and bake policies

| Setting | Purpose |
| --- | --- |
| **Open Operations Window** | Opens the [Static Instances operations window](OperationsWindow.md) for validation, bake, conversion and scene processing. Clicking it does not start a bake. |
| **Bake On Entering Play Mode** | Default **on**. Rebakes unbaked/stale targets in open scenes before ordinary Play. Serialized authoring data changes, so the scene is left dirty for you to save or discard. Independent of Bake On Build. Turning it off does not erase existing bakes. |
| **Bake On Build** | Default **on**. Repairs unbaked/stale targets on the build pipeline's scene copy, without saving changes back to the authoring scene asset. |

With Bake On Build disabled, unbaked content can collect at runtime, incurring
load-time allocation/work and retaining its sources. A stale baked target still
fails a normal Player build: the runtime cannot perform the Editor's asset-hash
validation. Resolve the build error or rebake; disabling this policy is not a stale
cache bypass.

## Stripping policies

| Setting | Purpose |
| --- | --- |
| **Strip Baked Sources On Build** | Default **on**. Removes redundant baked rendering sources from delivered scenes while preserving required physics/gameplay objects. Does not strip authoring scenes. Disable it for comparison builds that still need the original MeshRenderers; loading those sources gives up the source-stripping load benefit. |
| **Strip Unused Dots Instancing Variants** | Default **on**. Removes unused `DOTS_INSTANCING_ON` shader variants using delivered SIS content and the additional shader list. Applies only when every render pipeline asset referenced by the build has GPU Resident Drawer disabled. Independent of source stripping. |
| **Additional Dots Instancing Shaders** | Extra shaders whose DOTS-instancing variants must survive stripping, for example shaders used only by dynamically supplied content. Default **empty**. Displayed only when variant stripping is enabled. Add the required shader assets, not materials. |

> [!IMPORTANT]
> Keep Unity's separate **BatchRendererGroup Variants** setting at **Keep All**.
> SIS's selective stripping is applied afterward. These settings do not make
> simultaneous SIS and GPU Resident Drawer rendering a supported configuration.

### Addressables content builds

Addressables is optional. Bake and save delivered SIS scenes before building content.
For integrated validation and DOTS-variant stripping:

1. Create **Assets > Create > Addressables > Content Builders > Static Instances Packed Mode**.
2. Add the asset under **Build & Play Mode Scripts** in the Addressables settings.
3. Select it as the content builder and build the content.

With variant stripping enabled, the Graphics panel displays an information box when
Addressables is installed without an integrated builder. The stock unintegrated
builder keeps DOTS variants; it does not perform this SIS-specific stripping.
Legacy AssetBundles are outside this integration. Resolve failed build errors before
producing shipping bundles.

For custom build automation, call
`StaticInstanceBundleStripping.BeginContentBuild(scenePaths)` before the underlying
content build. A non-null return is a refusal reason: stop the build. Always call
`EndContentBuild()` in `finally`, including when Begin or the build fails. Implement
`IStaticInstanceStrippingIntegrated` for the settings hint. Integrated validation runs
before the Scriptable Build Pipeline (SBP) starts; a scene-processing callback
exception alone is not a reliable way to reject an SBP content build.

## Additional resources

- [Static Instances user guide](UserGuide.md): adopt a scene and use runtime APIs.
- [Static Instance Scatter Inspector](StaticInstanceScatterInspector.md): per-owner settings.
- [Static Instances operations window](OperationsWindow.md): commands, scope and save behavior.
