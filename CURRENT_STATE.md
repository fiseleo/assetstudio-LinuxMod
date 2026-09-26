# AssetStudio - Current State

**Last updated**: 2026-09-27, branch `linux-port` (based on v2.4.x, not pushed yet)
**For**: people and AI assistants picking up the work. History before the Linux port: `git log main`.

---

## Summary

- Runs on **Windows** (WinForms GUI `AssetStudio.GUI`, CLI) and **Linux x86-64** (Avalonia GUI `AssetStudio.Avalonia`,
  CLI, AppImage / `.deb`). Details: [LINUX.md](LINUX.md).
- Reads **Unity 2.x to Unity 6000.7**: SerializedFile formats up to 22, 23 (6000.5 type tree blobs) and 26
  (6000.7 shared sub trees); class layouts checked at every version they change (see [UNITY_6000_FIXES.md](UNITY_6000_FIXES.md)).
- Files **without type trees** are dumped / exported with the release type trees of their Unity version
  (`TypeTreeDatabase`, 200 KB TPK, Unity 3.4 to 6000.7.0a3), extensible with TypeTreeDumps InfoJson files for newer versions.
- **Addressables** catalogs (JSON and binary) give assets their address, labels and bundles.

## What works

| Area | State |
|------|-------|
| Textures | Texture2D, Sprite, SpriteAtlas; **Cubemap** (exported as a horizontal cross), **Texture2DArray / Texture3D** (a folder with an image per slice), **CubemapArray** (a cross per cube). GraphicsFormat (2019.1+) mapped to the decoders' TextureFormat |
| Shaders | Unity 5.x to 6000.7, including 6000.x player subprograms (`m_PlayerSubPrograms`). On Linux, DirectX programs (D3D9 bytecode, DXBC SM4/5, DXIL) → Vulkan SPIR-V (vkd3d-shader) → GLSL (SPIRV-Cross). Platform 28 = **D3D12** (6000.7) exported like D3D11. **WebGPU** (26): the WGSL of both stages (program type 33); Switch2 (27) like the consoles. Program parameters read from the blobs (`BlobProgramParameters`: after the byte code up to 2021.3.9, parameter entries from 2021.3.10) |
| Game shader preview (Avalonia) | Materials (on a sphere) and models drawn with their own shaders: forward pass and keyword variant chosen per material, **Direct3D 11** programs → SPIR-V (vkd3d) → Vulkan pipeline; constant buffers from Unity's layouts with built-in values (camera, light, SH) and material properties; textures, pass state (cull, reversed-Z depth, blend). `AssetStudio.Utility/ShaderRendering.cs`, `AssetStudio.Avalonia/ShaderPreview.cs`, `VulkanMeshRenderer.Shaded.cs` |
| MonoBehaviour | From the file's type tree (also **extracted type trees**: `.typetreedata` archives given with the files or next to them), or from assemblies (Load assembly folder / `--dummy_dlls`). **`[SerializeReference]`** registries: version 1 (2019.3 to 2021.1), 2 (2021.2+), 3 (6000.7, a frame in front of the first script field), with type trees and with assemblies |
| Other classes | Any class is dumped (`Dump`) and exported as JSON of its type tree; the CLI exports any class given with `--types`. **TerrainData**: heightmap as 16-bit PNG + Unity RAW + JSON |
| Models | FBX export (Windows and Linux) and **glTF 2.0** (.gltf / .glb, no native library): hierarchy, sub meshes, materials and textures, skins, blend shapes (morph targets), animations (transforms and blend shape weights). Avalonia preview: Vulkan (software fallback), textured, meshes placed by their hierarchy, **skinned, animations played** (Animator controller or legacy Animation clips), skeleton overlay, **blend shape sliders** (clips drive them too) |
| Video (Avalonia) | VideoClip / MovieTexture played in process with GStreamer (play, seek, loop, volume), or opened externally |
| Addressables | `catalog.json` (1.x to 4.x, from the first 2019 format) and `catalog.bin` (binary v1 to v3), given with the files, next to them (up to two folders above) or in catalog bundles. GUI: Address column, info, filter, export as JSON; CLI: `--containers` matches addresses, `AddressablesCatalogs.json` written |
| Audio | FMOD when present; without it FSB5 through Fmod5Sharp (Ogg / WAV). Linux preview through PulseAudio / PipeWire |
| GUI (Avalonia) | Same menus as Windows, Asset Browser, zoom / pan, theme (system / light / dark), fast regex filter on large lists |
| Performance | Parallel loading and export; `Logger.Verbose($"...")` doesn't format when verbose logging is off (loading 200k objects takes half the time) |

## Known gaps

- Extracted type trees are only found when the `.typetreedata` file is loaded with the files or sits in the same folder;
  otherwise a warning is logged and the type tree database / assemblies are used.
- D3D12 GPU program type numbers are not public (34 = vertex, 37 = pixel seen); every type after the known ones is accepted
  for platform 28. Switch2 (27) programs were not seen in a real file.
- Texture2DArray / CubemapArray: slices assumed one after the other, each with its mips (as UnityPy); pixel layout only
  verified with synthetic files (no public sample). Texture3D and Cubemap are verified with real files (keijiro's WebGPU samples).
- The type tree database ends at 6000.7.0a3 (TypeTreeDumps itself ends there, 2026-07): newer files without type trees
  are read with the latest known layouts unless newer InfoJson files are put in the TypeTreeDumps folder.
- Game shader preview: Direct3D 11 programs only (Windows builds and bundles); GLES, Metal, Vulkan and WebGPU programs,
  shadow maps, lightmaps, reflection probes and linear color space are not set up (grey sky, gamma). Models were only
  tested with a substituted material (no public Direct3D 11 bundle with a textured model was found).
- Video preview needs GStreamer (playbin, appsink); tested with generated mp4 / webm / ogv, no VideoClip from a real file.
- glTF: one shape per blend shape channel (the full weight in-between frame); materials map the base color, normal map and emission.
- Addressables: JSON catalogs of Addressables 1.19+ only tested through the format of the older ones and Unity's reader code.
- WinForms GUI: previews the new texture types and TerrainData (only compiled on Linux, not run); no animation preview.
- No automated tests in the repository (see *How things were verified*).

## How things were verified

- **Class layouts**: AssetRipper/TypeTreeDumps (`InfoJson/<version>.json`, one file per Unity version) — bisect the
  versions where a class's fields change, then compare AssetStudio's manual reader with a type tree read of the same objects.
- **Real samples**: UnityDataTools `TestCommon/Data` (player builds and bundles 2019.4 to 6000.7, `AssetBundleTypeTreeVariations`
  v22 / v23 inline and extracted / v26 with SerializeReference fixtures, `AssetBundles/2019.4.0f1`, `2020.3.0f1` with registry version 1), public demo bundles (katsumasa, euphoriaer, 764424567, plavip), a Unity 4.5 model.
- **Synthetic files**: SerializedFile (format 22) written from the exact release type trees of a version, with known
  pixel / height data, for classes without public samples (Texture2DArray, Texture3D, CubemapArray, TerrainData), at every layout change.
- **Type tree database**: for 1500+ objects with type trees, reading with the file's type tree and with the database give the same result.
- **glTF**: Khronos glTF-Validator (no errors), and the exported skins / animations evaluated against the preview's
  `ModelAnimator` at several times of every clip (4.5e-7 relative); morph targets compared with the mesh's blend shapes.
- **Addressables binary catalogs**: written by Unity's own serializer (Addressables 1.21.14, 1.21.18, 1.21.21, 2.11.2, 4.0.1
  sources compiled with stubs) and read back with every location, key and dependency equal.
- **Game shaders**: variants and parameters checked against the serialized parameters where both exist (5.6, 2019.4);
  renders of the D3D11 sample materials.
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
| Linux GUI | `AssetStudio.Avalonia/` (`Views/MainWindow.axaml(.cs)`, `MeshRenderer.cs`, `VulkanMeshRenderer.cs`, `ModelAnimator.cs`, `BlendShapeSet.cs`, `VideoPlayer.cs`, `ShaderPreview.cs`) |
| glTF export | `AssetStudio.Utility/GltfExporter.cs`, `ModelExporter.cs` (format switch) |
| Addressables | `AssetStudio/AddressablesCatalog.cs`, loaded by `AssetsManager` |
| Exporters (3 front ends) | `AssetStudio.CLI/Exporter.cs`, `AssetStudio.Avalonia/Exporter.cs`, `AssetStudio.GUI/Exporter.cs` |

## Next steps (candidates)

- Push the branch and run CI (Linux jobs never ran on GitHub).
- Move the verification harnesses (synthetic SerializedFile writer, type tree differential checks) into a test project.
- Animation preview in the WinForms GUI.
- Game shader preview for GLES / Vulkan / WebGPU programs (needs a GLSL / WGSL to SPIR-V compiler), shadows, linear color space.
