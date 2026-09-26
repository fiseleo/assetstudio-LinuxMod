# Unity Extractor Utility Asset Studio (multi-threaded)

**Version 2.4.0**

A Unity asset extraction tool supporting Unity 2.x through Unity 6 with multi-threaded loading and export capabilities.

**Latest improvements:**

- ✅ Linux: cross-platform GUI (`AssetStudio.Avalonia`), CLI, AppImage / `.deb`, Vulkan model preview ([LINUX.md](LINUX.md))
- ✅ Unity 6000.5 - 6000.7 files (SerializedFile formats 23 and 26), Unity 6 shaders fully parsed, D3D12 shader programs
- ✅ Cubemap, Texture2DArray, Texture3D, CubemapArray; TerrainData heightmaps; `[SerializeReference]` fields of MonoBehaviours
- ✅ Any class can be dumped or exported as JSON, also from games built without type trees
- ✅ Model preview (Linux GUI): skinned meshes, animation playback, skeleton
- ✅ Fixed parallel loading with duplicate CAB files (v2.4.0)
- ✅ Fixed Unity 6000 texture loading (v2.3.2)
- ✅ Multi-threaded parallel export for faster processing (v2.2.0)
- ✅ Thread-safety improvements (v2.2.1)
- ✅ Unity 6 TypeTree deserialization enhancements (v2.3.1)

## Download

Get the latest compiled releases from the [Releases](https://github.com/Razviar/assetstudio/releases) page.

The release package contains both:

- **AssetStudio.GUI.exe**: Graphical interface version
- **AssetStudio.CLI.exe**: Command-line version for automation

## Project History

This project has evolved through several iterations:

1. **Original**: [Perfare/AssetStudio](https://github.com/Perfare/AssetStudio) - The original AssetStudio project
2. **Fork**: [RazTools/Studio](https://github.com/RazTools/Studio) - Enhanced version with additional features
3. **Current**: Both upstream projects have paused active development, so we continue maintaining and updating this fork independently

Note: Requires Internet connection to fetch asset_index jsons.

## Features

### Performance

- **Parallel Bundle Loading**: Multi-threaded bundle decompression significantly reduces load times for games with many asset bundles (2-8x faster on multi-core systems)
- **Parallel Asset Export**: Multi-threaded export engine utilizing all CPU cores for 2-8x faster export speeds
- Optimized for batch processing large asset collections
- Thread-safe file operations prevent data corruption

### Unity Version Support

**Supported Versions**: Unity 2.x through Unity 6 (6000.0 - 6000.7)

- Class layouts are checked against Unity's own type trees at every version where they change
  (details in [UNITY_6000_FIXES.md](UNITY_6000_FIXES.md))
- Files built without type trees: the fields of any class come from a database of the release type trees of every
  Unity version (MonoBehaviour fields need the game's assemblies: *Load assembly folder* / `--dummy_dlls`)

#### Unity 6 Support (Added November 2025)

- Full support for Unity 6000.0.x - 6000.7.x series
- Version parsing handles new 6000.x.y format (replaces year-based 2023.x naming)
- Texture serialization updated for Unity 2023.2+ format changes (removed `m_ForcedFallbackFormat` and `m_DownscaleFallback` fields)
- Bundle loading, asset enumeration, and texture decoding all functional
- Known limitation: Some platform-specific texture compression formats may not decode correctly
- Unity 6000.5+ bundles with *extracted* type trees: load the `.typetreedata` file with them (or keep it in the same folder)

### Assets

- **Models**: FBX, or **glTF 2.0** (`.gltf` / `.glb`, no native library) with skins, blend shapes and animations
- **Addressables**: `catalog.json` / `catalog.bin` (loaded with the bundles or found next to them) give assets their address,
  labels and bundles
- **Shaders**: DirectX programs decompiled on Linux through Vulkan (vkd3d-shader + SPIRV-Cross), WebGPU programs as WGSL
- **WebGL / WebGPU builds**: `.data` / `.data.br` / `.data.unityweb` files load directly
- Linux GUI previews: videos (GStreamer), blend shapes, and models / materials drawn with the game's own shaders
  (Direct3D 11, Vulkan, OpenGL ES 3 / OpenGL and WebGPU programs, with the project's color space and shadows)

### User Experience

- **Interactive Version Prompt**: Automatic dialog for stripped Unity versions - no more error floods
- Version input applies globally to all subsequent files in batch operations

---

### Linux

A cross-platform GUI (`AssetStudio.Avalonia`) and the CLI run on Linux. See [LINUX.md](LINUX.md).

```bash
dotnet run --project AssetStudio.Avalonia   # run the GUI
./build-linux.sh                            # self-contained release in dist/
```

---

How to use:

Check the tutorial [here](https://gist.github.com/Modder4869/0f5371f8879607eb95b8e63badca227e) (Thanks to Modder4869 for the tutorial)

---

CLI Version:

```
Usage:
  AssetStudio.CLI <input_path> <output_path> [options]

Options (run with --help for all of them):
  --game <Normal|GI|SR|ZZZ|UnityCN|...> (REQUIRED)   Specify Game.
  --types <Texture2D|Shader:Parse|Sprite:Both|...>    Unity class type(s), space separated. Any class can be given:
                                                     classes without a converter are exported as JSON of their fields.
  --names <regex> / --containers <regex>             Name / container filters (containers also match Addressables addresses).
  --export_type <Convert|Raw|Dump|JSON>              How assets are exported. [default: Convert]
  --image_format <Png|Jpeg|Bmp|Tga>                  Texture export format. [default: Png]
  --group_assets <ByType|ByContainer|BySource|None>  How exported assets are grouped. [default: ByType]
  --unity_version <version>                          Unity version of stripped files.
  --map_op / --map_type / --map_name                 CABMap / AssetMap building.
  --key <key>                                        XOR key to decrypt MiHoYoBinData.
  --ai_file <path>                                   asset_index json (to recover GI containers).
  --dummy_dlls <folder>                              Assemblies, for MonoBehaviours without type trees.
  --model_format <Fbx|Gltf|Glb>                      Model export format. [default: Fbx]
  --typetree_dumps <folder>                          TypeTreeDumps InfoJson files for Unity versions newer than the built-in database.
  --logger_flags <Verbose|Debug|Info|...>            Log events to show.
```

Exports of the texture types: Cubemap = one horizontal cross image; Texture2DArray / Texture3D = a folder with an image
per slice; CubemapArray = a folder with a cross per cube. TerrainData = the heightmap as a 16-bit PNG and a Unity RAW, plus JSON.

---

NOTES:

```
- in case of any "MeshRenderer/SkinnedMeshRenderer" errors, make sure to enable "Disable Renderer" option in "Export Options" before loading assets.
- in case of need to export models/animators without fetching all animations, make sure to enable "Ignore Controller Anim" option in "Options -> Export Options" before loading assets.
```

---

Special Thank to:

- Perfare: Original author.
- Razmoth (Raz): Creator of [RazTools/Studio](https://github.com/RazTools/Studio) updated fork.
- Khang06: [Project](https://github.com/khang06/genshinblkstuff) for extraction.
- Radioegor146: [Asset-indexes](https://github.com/radioegor146/gi-asset-indexes) for recovered/updated asset_index's.
- Ds5678: [AssetRipper](https://github.com/AssetRipper/AssetRipper)[[discord](https://discord.gg/XqXa53W2Yh)] for information about Asset Formats & Parsing.
- mafaca: [uTinyRipper](https://github.com/mafaca/UtinyRipper) for `YAML` and `AnimationClipConverter`.
- [AssetRipper/TypeTreeDumps](https://github.com/AssetRipper/TypeTreeDumps) and [AssetRipper/Tpk](https://github.com/AssetRipper/Tpk) (MIT): the type tree database
  (`AssetStudio/Resources/lzma.tpk`, the one [UnityPy](https://github.com/K0lb3/UnityPy) (MIT) ships) and the class layouts of every Unity version.
- [Unity-Technologies/UnityDataTools](https://github.com/Unity-Technologies/UnityDataTools) test data, used to check Unity 6000.x support.
- [vkd3d](https://gitlab.winehq.org/wine/vkd3d) (LGPL-2.1) and [SPIRV-Cross](https://github.com/KhronosGroup/SPIRV-Cross) (Apache-2.0): DirectX shaders on Linux.
- [glslang](https://github.com/KhronosGroup/glslang) (BSD / MIT / Apache-2.0) and [naga](https://github.com/gfx-rs/wgpu/tree/trunk/naga) (MIT / Apache-2.0): GLSL and WGSL programs in the game shader preview.
