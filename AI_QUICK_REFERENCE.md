# AI Quick Reference - AssetStudio

**Last updated**: 2026-09-26 (branch `linux-port`). Longer context: [CURRENT_STATE.md](CURRENT_STATE.md),
[AI_ONBOARDING.md](AI_ONBOARDING.md), [UNITY_6000_FIXES.md](UNITY_6000_FIXES.md), [LINUX.md](LINUX.md).

---

## Current state

- Unity 2.x to **6000.7** (SerializedFile formats up to 26), Windows and Linux x86-64.
- Unity 6 shaders are **fully parsed** (the old "Unity 6000 shader bypass" of v2.3.2 is gone: do not bring it back).
- Files without type trees: `Object.Dump()` / `ToType()` fall back to `TypeTreeDatabase` (release type trees of every version).
- New readers on this branch: Cubemap, Texture2DArray, Texture3D, CubemapArray, `[SerializeReference]` registries,
  D3D12 shader programs, TerrainData heightmap (through its type tree).

---

## Critical file locations

| Area | Files |
|------|-------|
| Binary reading | `AssetStudio/EndianBinaryReader.cs` (`ReadAlignedString()` always aligns), `AssetStudio/ObjectReader.cs` (reads are bounded to the object) |
| Loading | `AssetStudio/AssetsManager.cs` (class switch in `ReadAssets`), `SerializedFile.cs`, `BundleFile.cs` |
| Type trees | `AssetStudio/TypeTreeHelper.cs` (Dump / JSON, SerializeReference), `TypeTreeDatabase.cs` + `Resources/lzma.tpk` |
| Classes | `AssetStudio/Classes/*.cs` (`Texture.cs` has `HasField()` and `ReadImageData()` helpers) |
| Type switches | `AssetStudio/TypeFlags.cs` — `AddedTypes`: defaults for types that older settings files don't list |
| Conversion | `AssetStudio.Utility/` (`Texture2DConverter`, `TextureImageExtensions`, `ShaderConverter`, `Vkd3dShader`, `ModelConverter`, `TerrainDataConverter`) |
| Front ends | CLI `AssetStudio.CLI/`, Linux GUI `AssetStudio.Avalonia/`, Windows GUI `AssetStudio.GUI/` — each has its own `Studio.cs` (asset list) and `Exporter.cs` |

A new exportable type usually touches: the class + `AssetsManager.ReadAssets`, `TypeFlags.AddedTypes`, the three
`Studio.ProcessAssetData` switches, the three `Exporter.ExportConvertFile` switches, the Avalonia preview, and the default
`types` JSON of each front end (`AssetStudio.Avalonia/Settings.cs`, `AssetStudio.CLI/App.config`, `AssetStudio.GUI/Properties/Settings.*`).

---

## Key technical details

### Version checks

```csharp
// version = { major, minor, build, type number }: 2022.3.76f1 -> {2022, 3, 76, 1}; Unity 6 -> 6000
if (version[0] > 2022 || (version[0] == 2022 && version[1] >= 2)) { } // 2022.2 and up
if (version[0] >= 6000) { }                                         // Unity 6
```

Fields that appeared in an alpha / beta: prefer the type tree when there is one (`Texture.HasField(reader, "m_X", byVersion)`).

### Finding layout changes

AssetRipper/TypeTreeDumps has `InfoJson/<version>.json` (release root node of every class, one file per Unity version).
Bisect between versions to find where a class's fields change, then read real objects both ways (manual reader vs
`TypeTreeHelper.ReadType`) and compare.

### Matrices

`AssetStudio.Matrix4x4` read from files (bind poses) has the translation in `M30..M32`: copy it as is into a
row-vector `System.Numerics.Matrix4x4` (as `ModelAnimator` and the FBX exporter do).

### Logging

`Logger.Verbose($"...")` uses an interpolated string handler: the message is not formatted when verbose logging is off.
Keep using interpolated strings there (building the string first defeats it).

---

## What NOT to do

- Don't change `ReadAlignedString()` (must always call `AlignStream()`) or remove `ObjectReader.Read()`'s bounds check.
- Don't reintroduce a "skip Unity 6000 shaders" bypass.
- Don't break parallel export: `Parallel.ForEach` in each `Studio.ExportAssets`; readers of one file share a stream,
  so type tree reads lock `reader.BaseStream` (`Object.Dump()` / `ToType()` do).
- FBX export is not thread-safe (the native exporter is serialized with a lock).
- Settings of older versions don't list new types: add new ones to `TypeFlags.AddedTypes`, not only to the default JSON.

---

## Quick problem solving

| Symptom | Look at |
|---------|---------|
| "Unable to read beyond the end of the stream" in a class | a field added / removed in that version: TypeTreeDumps bisect, compare with the Dump tab |
| "read X bytes but expected Y" when dumping | the type tree reader (`TypeTreeHelper`): managed references, extracted type trees |
| Object of a file without type trees shows nothing in Dump | `TypeTreeDatabase` (MonoBehaviour needs assemblies; versions after 6000.7.0a3 are approximated) |
| "type trees ... were extracted to a .typetreedata file" | load the build's `.typetreedata` with the files (`AssetsManager.LoadExtractedTypeTrees`) |
| DirectX shader not translated on Linux | `x64/libvkd3d-shader.so` / `libspirv-cross-c-shared.so` next to the executable |
| Model parts misplaced in the preview | `ModelAnimator` (frame hierarchy, bind poses) |

---

## Testing checklist

- [ ] `dotnet build AssetStudio.sln -c Release` (all front ends, also on Linux)
- [ ] Load samples of several versions (Unity 4/5, 2019-2022, 6000.0, 6000.5+, 6000.7); no new errors in the log
- [ ] Export Convert / Raw / Dump / JSON of the touched types
- [ ] Dump of files with type trees has no "read X bytes but expected Y"
- [ ] Parallel export still works (many assets)
- [ ] Avalonia preview of the touched types (headless harness or Xvfb + `dbus-run-session`)
