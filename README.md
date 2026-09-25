# Unity Extractor Utility Asset Studio (multi-threaded)

**Version 2.4.0**

A Unity asset extraction tool supporting Unity 2.x through Unity 6 with multi-threaded loading and export capabilities.

**Latest improvements:**

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

**Supported Versions**: Unity 2.x through Unity 6 (all 6000.x versions including 6000.0 - 6000.4+)

#### Unity 6 Support (Added November 2025)

- Full support for Unity 6000.0.x - 6000.4.x series (Unity 6 / Unity 6.1 / Unity 6.2 / Unity 6.3 / Unity 6.4)
- Version parsing handles new 6000.x.y format (replaces year-based 2023.x naming)
- Texture serialization updated for Unity 2023.2+ format changes (removed `m_ForcedFallbackFormat` and `m_DownscaleFallback` fields)
- Bundle loading, asset enumeration, and texture decoding all functional
- Known limitation: Some platform-specific texture compression formats may not decode correctly

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
Description:

Usage:
  AssetStudioCLI <input_path> <output_path> [options]

Arguments:
  <input_path>   Input file/folder.
  <output_path>  Output folder.

Options:
  --silent                                                Hide log messages.
  --type <Texture2D|Sprite|etc..>                         Specify unity class type(s)
  --filter <filter>                                       Specify regex filter(s).
  --game <BH3|CB1|CB2|CB3|GI|SR|TOT|ZZZ> (REQUIRED)       Specify Game.
  --image_format <Png|Jpeg|Bmp|Webp>                      Specify texture export format for Texture2D and Sprite assets. [default: Png]
  --map_op <AssetMap|Both|CABMap|None>                    Specify which map to build. [default: None]
  --map_type <JSON|XML>                                   AssetMap output type. [default: XML]
  --map_name <map_name>                                   Specify AssetMap file name.
  --group_assets_type <ByContainer|BySource|ByType|None>  Specify how exported assets should be grouped. [default: 0]
  --no_asset_bundle                                       Exclude AssetBundle from AssetMap/Export.
  --no_index_object                                       Exclude IndexObject/MiHoYoBinData from AssetMap/Export.
  --xor_key <xor_key>                                     XOR key to decrypt MiHoYoBinData.
  --ai_file <ai_file>                                     Specify asset_index json file path (to recover GI containers).
  --version                                               Show version information
  -?, -h, --help                                          Show help and usage information
```

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
