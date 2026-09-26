# AssetStudio - Current State

**Last updated**: 2026-09-26, branch `linux-port` (based on v2.4.x, not pushed yet)
**For**: people and AI assistants picking up the work. History before the Linux port: `git log main`.

---

## Summary

- Runs on **Windows** (WinForms GUI `AssetStudio.GUI`, CLI) and **Linux x86-64** (Avalonia GUI `AssetStudio.Avalonia`,
  CLI, AppImage / `.deb`). Details: [LINUX.md](LINUX.md).
- Reads **Unity 2.x to Unity 6000.7**: SerializedFile formats up to 22, 23 (6000.5 type tree blobs) and 26
  (6000.7 shared sub trees); class layouts checked at every version they change (see [UNITY_6000_FIXES.md](UNITY_6000_FIXES.md)).
- Files **without type trees** are dumped / exported with the release type trees of their Unity version
  (`TypeTreeDatabase`, 200 KB TPK, Unity 3.4 to 6000.7.0a3).

## What works

| Area | State |
|------|-------|
| Textures | Texture2D, Sprite, SpriteAtlas; **Cubemap** (exported as a horizontal cross), **Texture2DArray / Texture3D** (a folder with an image per slice), **CubemapArray** (a cross per cube). GraphicsFormat (2019.1+) mapped to the decoders' TextureFormat |
| Shaders | Unity 5.x to 6000.7, including 6000.x player subprograms (`m_PlayerSubPrograms`). On Linux, DirectX programs (D3D9 bytecode, DXBC SM4/5, DXIL) → Vulkan SPIR-V (vkd3d-shader) → GLSL (SPIRV-Cross). Platform 28 = **D3D12** (6000.7) exported like D3D11 |
| MonoBehaviour | From the file's type tree, or from assemblies (Load assembly folder / `--dummy_dlls`). **`[SerializeReference]`** registries: version 2 (2021.2+), version 3 (6000.7, a frame in front of the first script field), version 1 (2019.3 to 2021.1, untested) |
| Other classes | Any class is dumped (`Dump`) and exported as JSON of its type tree; the CLI exports any class given with `--types`. **TerrainData**: heightmap as 16-bit PNG + Unity RAW + JSON |
| Models | FBX export (Windows and Linux). Avalonia preview: Vulkan (software fallback), textured, meshes placed by their hierarchy, **skinned, animations played** (Animator controller or legacy Animation clips), skeleton overlay |
| Audio | FMOD when present; without it FSB5 through Fmod5Sharp (Ogg / WAV). Linux preview through PulseAudio / PipeWire |
| GUI (Avalonia) | Same menus as Windows, Asset Browser, zoom / pan, theme (system / light / dark), fast regex filter on large lists |
| Performance | Parallel loading and export; `Logger.Verbose($"...")` doesn't format when verbose logging is off (loading 200k objects takes half the time) |

## Known gaps

- **Extracted type trees** (6000.5+, Addressables "Extract Typetrees", `AssetBundle.typetreedata`) are not loaded: such
  files fall back to the type tree database (built-in classes) and assemblies (MonoBehaviour).
- `[SerializeReference]` with type trees generated from assemblies: only 2021.2 and up (registry version 2 / 3).
- D3D12 GPU program type numbers are not public (34 = vertex, 37 = pixel seen); every type after the known ones is accepted
  for platform 28. WebGPU (26) and Switch2 (27) programs are skipped.
- Texture2DArray / CubemapArray: slices assumed one after the other, each with its mips (as UnityPy); layouts verified,
  pixel layout only with synthetic files (no public sample).
- The type tree database ends at 6000.7.0a3: newer files without type trees are read with the latest known layouts (a warning is logged).
- WinForms GUI: exports the new texture types, but its preview shows a Cubemap as its first face; no animation preview.
- No automated tests in the repository (see *How things were verified*).

## How things were verified

- **Class layouts**: AssetRipper/TypeTreeDumps (`InfoJson/<version>.json`, one file per Unity version) — bisect the
  versions where a class's fields change, then compare AssetStudio's manual reader with a type tree read of the same objects.
- **Real samples**: UnityDataTools `TestCommon/Data` (player builds and bundles 2019.4 to 6000.7, `AssetBundleTypeTreeVariations`
  v22 / v23 / v26 with SerializeReference fixtures), public demo bundles (katsumasa, euphoriaer, 764424567, plavip), a Unity 4.5 model.
- **Synthetic files**: SerializedFile (format 22) written from the exact release type trees of a version, with known
  pixel / height data, for classes without public samples (Texture2DArray, Texture3D, CubemapArray, TerrainData), at every layout change.
- **Type tree database**: for 1500+ objects with type trees, reading with the file's type tree and with the database give the same result.
- **GUI**: an Avalonia.Headless harness (drives `MainWindow`, captures the previews). When starting the real GUI for
  tests, use Xvfb, `dbus-run-session` and a scratch `XDG_CONFIG_HOME`, so dialogs and settings don't reach the desktop session.
- DXIL: programs compiled with Microsoft's `dxc` for Linux.

## Build & run

```bash
dotnet build AssetStudio.sln -c Release          # everything (WinForms is compile-only on Linux)
dotnet run --project AssetStudio.Avalonia         # Linux GUI
dotnet run --project AssetStudio.CLI -f net8.0 -- <input> <output> --game Normal
./build-linux.sh && ./build-packages.sh           # Linux release folder, AppImage, .deb
```

Windows: open `AssetStudio.sln`, build `AssetStudio.GUI` / `AssetStudio.CLI` (net8.0-windows, or net10.0 with the .NET 10 SDK).

## Where things are

| What | Where |
|------|-------|
| Asset loading | `AssetStudio/AssetsManager.cs`, `SerializedFile.cs`, `BundleFile.cs` |
| Type tree reading (SerializeReference) | `AssetStudio/TypeTreeHelper.cs` |
| Type tree database | `AssetStudio/TypeTreeDatabase.cs`, `AssetStudio/Resources/lzma.tpk` |
| Texture classes | `AssetStudio/Classes/Texture*.cs`, `Cubemap*.cs`, `GraphicsFormat.cs`; images: `AssetStudio.Utility/TextureImageExtensions.cs` |
| Shaders | `AssetStudio/Classes/Shader.cs`, `AssetStudio.Utility/ShaderConverter.cs`, `Vkd3dShader.cs` |
| TerrainData | `AssetStudio.Utility/TerrainDataConverter.cs` |
| Parse / export type switches | `AssetStudio/TypeFlags.cs` (types added later are enabled for old settings files) |
| Linux GUI | `AssetStudio.Avalonia/` (`Views/MainWindow.axaml(.cs)`, `MeshRenderer.cs`, `VulkanMeshRenderer.cs`, `ModelAnimator.cs`) |
| Exporters (3 front ends) | `AssetStudio.CLI/Exporter.cs`, `AssetStudio.Avalonia/Exporter.cs`, `AssetStudio.GUI/Exporter.cs` |

## Next steps (candidates)

- Push the branch and run CI (Linux jobs never ran on GitHub).
- Load `.typetreedata` files (extracted type trees).
- Move the verification harnesses (synthetic SerializedFile writer, type tree differential checks) into a test project.
- Wire the new texture types and animation preview into the WinForms GUI.
