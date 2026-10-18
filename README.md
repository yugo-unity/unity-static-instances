English | [日本語](README.ja.md)

# Static Instances

Static scene instance rendering for Unity URP, with per-instance mesh LOD,
visibility, shadows and authoring tools. The package provides the
**Static Instance Scatter (SIS)** component, which renders static MeshRenderer
content through the BatchRendererGroup (BRG) API.

> [!NOTE]
> Version **0.1.0** is an early release. APIs, serialized data and defaults may
> change before 1.0.

## Requirements

- Unity **6000.3** or later with the Universal Render Pipeline (URP) package that
  matches the Editor (URP **17.3.0** or later).
- Burst, Collections and Mathematics are installed automatically as dependencies.
- Addressables is optional. Its integration compiles only when a compatible
  Addressables package is installed.

The validated environment is Windows with Direct3D 12. Other platforms and
graphics APIs are not currently qualified.

## Installation

In **Window > Package Manager**, choose **+ > Install package from git URL…** and
enter:

```
https://github.com/yugo-unity/unity-static-instances.git
```

To pin a release, append its tag, for example
`https://github.com/yugo-unity/unity-static-instances.git#v0.1.0`. You can also add
the same URL to the `dependencies` of your project's `Packages/manifest.json`:

```json
"jp.fuji0k0.static-instances": "https://github.com/yugo-unity/unity-static-instances.git#v0.1.0"
```

For local development, clone the repository and use **Install package from disk…**
with its `package.json`.

## Project setup

1. Assign a URP Asset in **Project Settings > Graphics** and **Quality**.
2. In **Project Settings > Graphics > Shader Stripping**, set **BatchRendererGroup
   Variants** to **Keep All**.
3. Do not enable GPU Resident Drawer (GRD) for content rendered by SIS. Rendering
   the same content with SIS and GRD at the same time is not supported.

## First scene

1. Save a test scene. Put opaque, BRG/DOTS-instancing-compatible MeshRenderers
   below a parent and add **Rendering > Static Instance Scatter** to it.
   Keep each source's **Batching Static** flag off and do not nest SIS owners.
2. Open **Window > Rendering > Static Instances**, run **Validate**, and resolve
   unsupported content. Select the saved scene and use **Bake Selected
   Scenes** to bake and save it.
3. Enter Play and inspect the result. Keep gameplay and physics objects required
   by the scene; review source stripping before building a Player.

Transforms, meshes, materials and rendering attributes are immutable after
initialization. Authoring changes require rebaking and reinitialization. LOD is
off by default. With authored mesh LOD levels, start with **Screen Size** selection
and **Time** fades; Draw Range works independently of mesh LOD.

**Motion Vector** is a default-off, initialization-time option for shader vertex
animation, such as VAT. The shader must expose a pass tagged
with `LightMode=MotionVectors` and calculate both current and previous deformed
positions; enabling the option does not animate materials or instance transforms.

SpeedTree 9 content is drawn static: SIS does not simulate wind, and the explicit
bake operations generate wind-stripped materials for it.

## Documentation

- [User guide](Docs/UserGuide.md): scene adoption, runtime API and workflows.
- [SIS Inspector](Docs/StaticInstanceScatterInspector.md): per-component settings.
- [Graphics Settings](Docs/GraphicsSettings.md): project-wide quality, bake and
  stripping policies.
- [Operations window](Docs/OperationsWindow.md): validation, baking, conversion and
  scene batches.

## Limitations

- Not supported: hierarchical LOD (HLOD), nested SIS owners, skinned or transparent
  rendering, lightmaps, direct LODGroup rendering (convert them first),
  per-renderer MaterialPropertyBlock variations, and simultaneous SIS/GRD rendering
  of the same content.
- World-space bakes are not relocatable prefab data.
- No general performance advantage over GRD and no zero-allocation guarantee is
  claimed. Profile with your own content.

Verify saved bakes, shader inclusion, stripping, cameras and the actual Player for
your content before shipping.

## Tests

Editor tests are included. Add the package to `testables` at the root of your
project's `Packages/manifest.json` to show them in the Test Runner:

```json
"testables": ["jp.fuji0k0.static-instances"]
```

Use an assigned URP Asset when running the BRG lifecycle tests.

## License

[MIT](LICENSE)
