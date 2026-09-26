# AI Assistant Onboarding Guide for AssetStudio

**Last Updated**: November 19, 2025  
**Project Version**: 2.3.1  
**Purpose**: This document provides AI assistants with comprehensive context to effectively contribute to the AssetStudio project.

---

## Table of Contents

1. [Project Overview](#project-overview)
2. [Architecture](#architecture)
3. [Critical Concepts](#critical-concepts)
4. [Development Guidelines](#development-guidelines)
5. [Common Tasks](#common-tasks)
6. [Troubleshooting Guide](#troubleshooting-guide)
7. [Version History & Context](#version-history--context)

---

## Project Overview

### What is AssetStudio?

AssetStudio is a **Unity asset extraction tool** that reads Unity game files and exports assets (textures, models, audio, shaders, etc.) to standard formats. It's a reverse-engineering tool used by modders, researchers, and game archivists.

### Key Facts

- **Language**: C# (.NET 8; .NET 10 targets are added when the .NET 10 SDK is installed)
- **Supported Unity Versions**: Unity 2.x through Unity 6000.7 (SerializedFile formats up to 26)
- **Primary Users**: Game modders, asset extractors, researchers
- **Interface**: CLI (AssetStudio.CLI, Windows and Linux), Windows GUI (AssetStudio.GUI, WinForms),
  cross-platform GUI (AssetStudio.Avalonia, used on Linux)
- **Critical Feature**: Multi-threaded loading/export for performance

### Project Structure

```
AssetStudio/                    # Core library (asset parsing logic)
├── Classes/                    # Unity class deserializers
│   ├── Shader.cs              # Shader parsing (Unity 5.x - 6000.7)
│   ├── Texture2D.cs           # Texture parsing (Cubemap derives from it)
│   ├── Texture2DArray.cs, Texture3D.cs, CubemapArray.cs, GraphicsFormat.cs
│   ├── Mesh.cs                # 3D model parsing
│   └── ...
├── AssetsManager.cs           # Main orchestrator (parallel loading)
├── SerializedFile.cs          # Unity asset file parser (formats up to 26)
├── BundleFile.cs              # Unity bundle decompression
├── TypeTree.cs                # Runtime type information
├── TypeTreeHelper.cs          # Reads objects through a type tree (Dump / JSON, [SerializeReference])
├── TypeTreeDatabase.cs        # Release type trees of every Unity version (Resources/lzma.tpk)
└── TypeFlags.cs               # Which types are parsed / exported

AssetStudio.CLI/               # Command-line interface
├── Program.cs                 # CLI entry point
└── Exporter.cs                # Batch export logic

AssetStudio.GUI/               # Windows Forms GUI (Windows only)
├── MainForm.cs                # Main window
├── AssetBrowser.cs            # Asset list view
└── Studio.cs                  # GUI-specific orchestration

AssetStudio.Avalonia/          # Cross-platform GUI (Linux), same menus as the WinForms one
├── Views/MainWindow.axaml(.cs)
├── Studio.cs, Exporter.cs
├── MeshRenderer.cs            # Model preview (software renderer)
├── VulkanMeshRenderer.cs      # Model preview on the GPU (Silk.NET.Vulkan)
└── ModelAnimator.cs           # Frame hierarchy, skinning and animation playback for the preview

AssetStudio.Utility/           # Export utilities
├── Texture2DConverter.cs      # Image export (PNG, JPEG, etc.)
├── TextureImageExtensions.cs  # Faces / slices of Cubemap, Texture2DArray, Texture3D, CubemapArray
├── ModelConverter.cs          # 3D model export (FBX, OBJ)
├── AudioClipConverter.cs      # Audio export (WAV, MP3)
├── ShaderConverter.cs         # Shader text export
├── Vkd3dShader.cs             # DirectX -> Vulkan SPIR-V -> GLSL (Linux)
└── TerrainDataConverter.cs    # TerrainData heightmap

AssetStudio.FBXWrapper/        # FBX export wrapper
AssetStudio.FBXNative/         # Native FBX library (C++)
AssetStudio.PInvoke/           # Platform invoke utilities
```

### Current State

See [CURRENT_STATE.md](CURRENT_STATE.md) (kept up to date) and [AI_QUICK_REFERENCE.md](AI_QUICK_REFERENCE.md).
In short: Unity 2.x to 6000.7 are read, Unity 6 shaders are fully parsed (the v2.3.x error-tolerant workarounds
described below are history), the tool runs on Windows and Linux.

---

## Architecture

### Data Flow

```
User Input (Files/Folders)
    ↓
AssetsManager.LoadFiles()
    ↓
Parallel Bundle Decompression (BundleFile.cs)
    ↓
SerializedFile.Parse() → TypeTree reading
    ↓
Object Deserialization (Classes/*.cs)
    ↓
AssetBrowser Display / Export Queue
    ↓
Parallel Export (Exporter.cs)
    ↓
Output Files (PNG, FBX, OBJ, etc.)
```

### Key Classes

#### **AssetsManager** (`AssetsManager.cs`)

- **Purpose**: Orchestrates file loading, parallel processing, dependency resolution
- **Critical Methods**:
  - `LoadFiles()`: Entry point for loading files/folders
  - `LoadAssetsFile()`: Parses individual Unity asset files
  - `ProcessAssets()`: Deserializes objects in parallel
- **Threading**: Extensive use of `Task.Run()`, `Parallel.ForEach()`
- **Thread Safety**: Uses locks (`assetsFileListLock`, `importFilesLock`)

#### **SerializedFile** (`SerializedFile.cs`)

- **Purpose**: Low-level Unity asset file parser
- **Key Concepts**:
  - Reads file headers (version, endianness, platform)
  - Parses TypeTree (runtime type information)
  - Builds object table (m_Objects)
- **Version Detection**: Critical for format differences across Unity versions

#### **ObjectReader** (`ObjectReader.cs`)

- **Purpose**: Wrapper around EndianBinaryReader with version context
- **Usage**: All deserialization classes receive `ObjectReader reader`
- **Version Access**: `reader.version` array (e.g., `[6000, 0, 58, 2]` for Unity 6000.0.58f2)

#### **TypeTree** (`TypeTree.cs`, `TypeTreeNode.cs`)

- **Purpose**: Stores runtime type structure for Unity classes
- **Why Important**: Allows deserialization of unknown/modified Unity classes
- **Format**: Tree of nodes describing field names, types, sizes
- **Problem Area**: Unity 6 has different TypeTree structures for some classes

### Threading Model

**AssetStudio is HEAVILY multi-threaded:**

1. **Bundle Decompression**: Parallel decompression of `.bundle` files
2. **Asset File Loading**: Multiple files loaded concurrently
3. **Object Deserialization**: Objects processed in parallel batches
4. **Asset Export**: Export operations parallelized per-asset

**Thread Safety Requirements:**

- All writes to `assetsFileList` use locks
- Resource readers stored in `ConcurrentDictionary`
- Logger must be thread-safe
- Object constructors should be stateless (read-only operations)

---

## Critical Concepts

### Unity Serialization Format

Unity stores assets in a binary format with:

- **Header**: Magic bytes, version, endianness, platform
- **Type Metadata**: TypeTree describing object structure
- **Object Table**: List of all objects with offsets
- **Object Data**: Serialized binary data

**Key Insight**: Format changes between Unity versions WITHOUT clear documentation.

### Version Handling

Unity version stored as `int[]`:

```csharp
// Unity 2021.3.10f1 → [2021, 3, 10, 1]
// Unity 6000.0.58f2 → [6000, 0, 58, 2]
reader.version[0]  // Major (2021 or 6000)
reader.version[1]  // Minor
reader.version[2]  // Patch
reader.version[3]  // Type (0=a, 1=b, 2=f, 3=p)
```

**Critical Pattern:**

```csharp
if (version[0] > 2020 || (version[0] == 2020 && version[1] >= 2))
{
    // 2020.2 and up
}

if (version[0] >= 6000)
{
    // Unity 6 specific
}
```

### Error Handling Philosophy

**Graceful Degradation Over Crashes:**

- Prefer partial data over total failure
- Use try-catch in deserializers to continue loading
- Log errors at appropriate levels:
  - `Logger.Error()`: Critical failures (file corruption)
  - `Logger.Warning()`: Parse failures but recoverable
  - `Logger.Verbose()`: Format differences, debug info
  - `Logger.Info()`: Normal operations

**Example** (from Shader.cs):

```csharp
public Shader(ObjectReader reader) : base(reader)
{
    try
    {
        // Parse shader data
        m_ParsedForm = new SerializedShader(reader);
        // ... more parsing
    }
    catch (Exception ex)
    {
        Logger.Warning($"Failed to parse Shader (Unity {version}): {ex.Message}");
        // Shader still loads with basic info (name, type)
    }
}
```

### Game-Specific Handling

**Special Games** (MiHoYo, Unity China):

```csharp
public enum Game
{
    BH3,      // Honkai Impact 3rd
    GI,       // Genshin Impact
    SR,       // Honkai: Star Rail
    ZZZ,      // Zenless Zone Zero
    CB1, CB2, CB3,  // Closed Beta versions
    TOT,      // Tears of Themis
}
```

**These games use**:

- Encrypted bundles (XOR encryption)
- Custom file formats (MhyFile.cs, BlbFile.cs)
- Asset index files (to recover stripped containers)

### TypeTree vs. Class-Based Deserialization

**Two Modes:**

1. **Class-Based** (Preferred):

   - Handwritten C# classes (e.g., `Shader.cs`, `Texture2D.cs`)
   - Fast, type-safe, IDE-friendly
   - Requires updating when Unity format changes

2. **TypeTree-Based** (Fallback):
   - Generic dictionary-based deserialization
   - Slower, untyped (everything is `object`)
   - Works for unknown classes or when format changes

**When to Use Each:**

- Use class-based for common assets (Texture2D, Mesh, Material, etc.)
- TypeTree for rare/custom classes or MonoBehaviour scripts

---

## Development Guidelines

### Code Style

**Follow existing patterns:**

```csharp
// Naming conventions
public class SerializedShader           // PascalCase for classes
private List<SerializedPass> m_Passes;  // m_ prefix for fields
public int PassCount { get; }           // PascalCase for properties

// Version checks (always check major first)
if (version[0] > 2020 || (version[0] == 2020 && version[1] >= 2))
{
    // New format
}

// Alignment after reading variable-length data
reader.AlignStream();  // Aligns to 4-byte boundary

// Array reading
int count = reader.ReadInt32();
List<MyType> items = new List<MyType>();
for (int i = 0; i < count; i++)
{
    items.Add(new MyType(reader));
}
```

### Adding Unity Version Support

**When Unity releases a new version:**

1. **Test with real game files** (not Unity Editor exports)
2. **Check for format changes**:
   - Look for TypeTree structure differences
   - Monitor log for parse failures
   - Compare hex dumps between versions
3. **Update version checks** in affected classes
4. **Add error handling** for unknown fields
5. **Update README** with version support

**Example** (adding Unity 2024 support):

```csharp
// Before
if (version[0] >= 2023)
{
    // 2023 format
}

// After
if (version[0] >= 2024)
{
    // 2024 format
    var newField = reader.ReadInt32();
}
else if (version[0] >= 2023)
{
    // 2023 format
}
```

### Shader Format Changes (Critical Area)

Shader layouts changed in many 6000.x releases (see the table in [UNITY_6000_FIXES.md](UNITY_6000_FIXES.md)). They are
now read exactly; the v2.3.x "skip / tolerate" handling is gone. When a new Unity version breaks shaders:

1. Find the change in the type tree dumps (AssetRipper/TypeTreeDumps `InfoJson`), bisecting over versions
2. Update `Shader.cs` with a version check (or `HasField` on the type tree for alpha / beta changes)
3. Compare the manual read with `TypeTreeHelper.ReadType` on real files of that version
4. Export and check the programs: 6000.x players keep them in `m_PlayerSubPrograms`; platform 28 is D3D12

### Testing Approach

**No automated unit tests** in the repository. What was used on the `linux-port` branch (details in
[CURRENT_STATE.md](CURRENT_STATE.md)): UnityDataTools `TestCommon/Data` samples (2019.4 to 6000.7), type tree dumps to find
layout changes, differential checks (manual reader vs type tree, file type tree vs `TypeTreeDatabase`), synthetic
SerializedFiles written from the release type trees for classes without samples, and an Avalonia.Headless harness.

**Manual Testing:**

1. **Get test files**: Download Unity games (Steam, Epic, mobile games)
2. **Load in AssetStudio GUI**: Check console output for errors
3. **Check error counts**: Compare before/after changes
4. **Export assets**: Ensure output files are valid
5. **Test multiple Unity versions**: 2019, 2020, 2021, 2022, 2023, Unity 6

**Common Test Games:**

- **Genshin Impact**: Unity 2020.3 (MiHoYo encryption)
- **Marvel Snap**: Unity 6000.0.58f2 (latest format)
- **Among Us**: Unity 2019.4 (simple, good baseline)

### Performance Considerations

**This tool processes GIGABYTES of data:**

1. **Use parallel processing**: Already done in AssetsManager
2. **Avoid unnecessary allocations**: Reuse buffers where possible
3. **Stream large data**: Don't load entire files into memory
4. **Cache lookups**: `assetsFileIndexCache`, `resourceFileReaders`
5. **Lock minimally**: Hold locks as briefly as possible

**Example** (efficient array reading):

```csharp
// Good: Preallocate
var items = new List<SerializedPass>(numPasses);
for (int i = 0; i < numPasses; i++)
{
    items.Add(new SerializedPass(reader));
}

// Bad: Growing list
var items = new List<SerializedPass>();  // Default capacity too small
```

### Logging Best Practices

**Use appropriate log levels:**

```csharp
Logger.Error("Failed to open file: {0}", path);      // File I/O failures
Logger.Warning("Failed to parse Shader (Unity 6)");   // Parse failures (recoverable)
Logger.Info("Loaded 1,234 assets from bundle");       // User-facing progress
Logger.Verbose("SerializedPass: version 6000.0.58");  // Debug info (disabled by default)
```

**Include context in messages:**

```csharp
// Good
Logger.Warning($"Failed to parse Shader '{m_Name}' (Unity {version[0]}.{version[1]}.{version[2]}): {ex.Message}");

// Bad
Logger.Warning("Parse failed");
```

---

## Common Tasks

### Task 1: Adding Support for a New Unity Class

**Example**: Adding support for `MyNewAsset` class

1. **Create class file**: `AssetStudio/Classes/MyNewAsset.cs`

```csharp
namespace AssetStudio
{
    public class MyNewAsset : Object
    {
        public string m_Name;
        public int m_SomeValue;
        public List<PPtr<Texture2D>> m_Textures;

        public MyNewAsset(ObjectReader reader) : base(reader)
        {
            m_Name = reader.ReadAlignedString();
            m_SomeValue = reader.ReadInt32();

            int textureCount = reader.ReadInt32();
            m_Textures = new List<PPtr<Texture2D>>();
            for (int i = 0; i < textureCount; i++)
            {
                m_Textures.Add(new PPtr<Texture2D>(reader));
            }
        }
    }
}
```

2. **Register it** in the class switch of `AssetsManager.ReadAssets`, and in `TypeFlags.AddedTypes` (settings files of
   older versions don't list it), then in the `ProcessAssetData` / `ExportConvertFile` switches of the three front ends
   (`AssetStudio.CLI`, `AssetStudio.Avalonia`, `AssetStudio.GUI`)

3. **Test with real Unity files** of the versions where the class changes

A class without its own reader can still be dumped and exported as JSON through its type tree (the file's, or
`TypeTreeDatabase` for files built without type trees); TerrainData works that way.

### Task 2: Fixing Version-Specific Parse Failures

**Example**: Unity 2024 adds new fields to Texture2D

1. **Analyze error logs**:

   ```
   [Error] Failed to parse Texture2D: Unable to read beyond end of stream
   ```

2. **Find affected class**: `AssetStudio/Classes/Texture2D.cs`

3. **Add version check**:

   ```csharp
   // Existing fields
   m_Width = reader.ReadInt32();
   m_Height = reader.ReadInt32();

   // Add new field for Unity 2024+
   if (version[0] >= 2024)
   {
       m_NewFieldInUnity2024 = reader.ReadInt32();
   }

   // Continue with existing fields
   m_Format = (TextureFormat)reader.ReadInt32();
   ```

4. **Test with Unity 2024 game files**

### Task 3: Improving Multi-Threading Performance

**Example**: Speed up texture export

1. **Profile** (add timing logs):

   ```csharp
   var sw = Stopwatch.StartNew();
   Parallel.ForEach(textures, texture => {
       ExportTexture(texture);
   });
   Logger.Info($"Exported {textures.Count} textures in {sw.ElapsedMilliseconds}ms");
   ```

2. **Identify bottlenecks**: File I/O, image encoding, etc.

3. **Optimize** (example: batch file writes):

   ```csharp
   // Before: Open/close file per texture
   File.WriteAllBytes(path, imageData);

   // After: Use buffered stream
   using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
   {
       fs.Write(imageData, 0, imageData.Length);
   }
   ```

### Task 4: Adding Error Handling to a Class

**Example**: Make `Material.cs` resilient to Unity 6 changes

1. **Wrap constructor in try-catch**:

   ```csharp
   public Material(ObjectReader reader) : base(reader)
   {
       try
       {
           // All parsing logic
           m_Shader = new PPtr<Shader>(reader);
           m_SavedProperties = new UnityPropertySheet(reader);
       }
       catch (Exception ex)
       {
           Logger.Warning($"Failed to fully parse Material (Unity {reader.version[0]}.{reader.version[1]}): {ex.Message}");
           // Material still accessible with partial data
       }
   }
   ```

2. **Add nested try-catch for complex sub-objects** (if needed)

3. **Test that partial data is still useful**

### Task 5: Investigating Unknown Format Changes

**Example**: Unity 6 shader format mystery

1. **Collect error samples**:

   ```powershell
   Select-String -Path log.txt -Pattern "Failed to parse" -Context 3,3
   ```

2. **Look for patterns**:

   - Same error across multiple assets?
   - Specific Unity version?
   - Occurs at specific field reads?

3. **Hex dump comparison**:

   - Export same asset from Unity 5 vs Unity 6
   - Compare binary structure
   - Look for added/removed fields

4. **Add debug logging**:

   ```csharp
   Logger.Verbose($"About to read field X at position {reader.Position}");
   var value = reader.ReadInt32();
   Logger.Verbose($"Read value: {value}");
   ```

5. **Implement best-guess format** with error handling

---

## Troubleshooting Guide

### Problem: "Unable to read beyond the end of the stream"

**Causes:**

1. Unity version format changed (fields added/removed)
2. Incorrect version detection
3. Alignment issue (forgot `reader.AlignStream()`)

**Solutions:**

1. Check `reader.version` at error location
2. Compare with similar Unity versions
3. Add version-specific code path
4. Wrap in try-catch for graceful degradation

**Example**:

```csharp
// Before (crashes on Unity 6)
m_SomeField = reader.ReadInt32();

// After (handles Unity 6)
if (version[0] >= 6000)
{
    m_Unity6Field = reader.ReadInt32();  // New field in Unity 6
}
m_SomeField = reader.ReadInt32();
```

### Problem: "String length [huge number] exceeds remaining bytes"

**Causes:**

1. Reading at wrong stream position (missed fields)
2. Alignment issue
3. Format changed and we're reading data as string when it's not

**Solutions:**

1. Check if fields were added BEFORE the string read
2. Add `reader.AlignStream()` before string read
3. Wrap in try-catch and log position:
   ```csharp
   try
   {
       var str = reader.ReadAlignedString();
   }
   catch
   {
       Logger.Verbose($"Failed at position {reader.Position}, expected string");
   }
   ```

### Problem: Multi-Threading Deadlock or Crash

**Causes:**

1. Accessing shared resource without lock
2. Lock ordering issue (lock A then B, vs lock B then A)
3. Non-thread-safe Logger usage

**Solutions:**

1. Use locks for all shared collections
2. Keep consistent lock ordering
3. Use thread-safe data structures (`ConcurrentDictionary`)
4. Ensure Logger is thread-safe

### Problem: Assets Export as Corrupt/Invalid Files

**Causes:**

1. Incorrect deserialization (read wrong data)
2. Texture compression format not supported
3. Endianness issue

**Solutions:**

1. Check deserialization against Unity documentation
2. Check `m_TextureFormat` and ensure converter supports it
3. Verify `reader.endian` matches file endianness

---

## Version History & Context

### linux-port branch (September 2026)

- Linux: `AssetStudio.Avalonia` GUI, CLI on Linux, AppImage / `.deb`, Vulkan model preview, DirectX shaders translated to Vulkan GLSL
- Unity 6000.5 to 6000.7 (SerializedFile formats 23 and 26, class layouts), Unity 6 shaders fully parsed, D3D12 programs
- `[SerializeReference]`, Cubemap / Texture2DArray / Texture3D / CubemapArray, TerrainData heightmap
- Any class dumped / exported as JSON, also from files without type trees (`TypeTreeDatabase`)
- Avalonia: animation playback and skeleton in the model preview, themes

### v2.3.1 (November 19, 2025)

**Changes:**

- Added TypeTree deserialization error handling (graceful skipping)
- Implemented 4-layer error handling in Shader classes
- Improved Unity 6 resilience (reduced crashes from 1,290 → 1,082)

**Known Issues:**

- 1,082 shaders still fail to fully parse on Unity 6000.0.58f2
- Unity 6 format changes undocumented

**Context:**

- Marvel Snap game (Unity 6000.0.58f2) revealed shader format issues
- Log analysis showed 22,361 errors (21,989 TypeTree, 1,082 shaders)
- Graceful degradation preferred over crash-and-burn

### v2.3.0 (November 15, 2025)

**Major Changes:**

- Unity 6 (6000.x) version support
- .NET 10 upgrade (from .NET 8)
- Texture2D format updates for Unity 2023.2+

**Context:**

- Unity 6 uses 6000.x.y versioning (not year-based)
- Removed deprecated Texture2D fields

### v2.2.x and Earlier

**History:**

- Originally forked from Perfare/AssetStudio
- Enhanced by RazTools with multi-threading
- Continued development after upstream pause

**Key Features Added Over Time:**

- Parallel bundle loading (2-8x speedup)
- Game-specific support (MiHoYo games)
- Interactive version prompt for stripped builds
- CLI interface for automation

---

## Quick Reference

### File Locations

| What                 | Where                               |
| -------------------- | ----------------------------------- |
| Version number       | `VERSION` file (root)               |
| Class deserializers  | `AssetStudio/Classes/*.cs`          |
| Main orchestration   | `AssetStudio/AssetsManager.cs`      |
| GUI main form        | `AssetStudio.GUI/MainForm.cs`       |
| CLI entry point      | `AssetStudio.CLI/Program.cs`        |
| Export logic         | `AssetStudio.Utility/*Converter.cs` |
| Release instructions | `RELEASE.md`                        |

### Important Patterns

```csharp
// Reading arrays
int count = reader.ReadInt32();
for (int i = 0; i < count; i++) { /* read items */ }

// Version checks
if (version[0] >= 6000) { /* Unity 6+ */ }
if (version[0] > 2020 || (version[0] == 2020 && version[1] >= 2)) { /* 2020.2+ */ }

// Alignment
reader.AlignStream();  // After variable-length data (strings, arrays)

// Error handling
try { /* parse */ }
catch (Exception ex) { Logger.Warning($"Parse failed: {ex.Message}"); }

// PPtr (Unity object reference)
var shader = new PPtr<Shader>(reader);  // Deserializes reference
var actualShader = shader.TryGet(assetsFile);  // Resolves to actual object
```

### Common Unity Types

| C# Type             | Unity Type       | Notes                                  |
| ------------------- | ---------------- | -------------------------------------- |
| `PPtr<T>`           | Object reference | `m_FileID` + `m_PathID`                |
| `Vector3`           | 3D vector        | 3 floats (x, y, z)                     |
| `Quaternion`        | Rotation         | 4 floats (x, y, z, w)                  |
| `Matrix4x4`         | Transform matrix | 16 floats                              |
| `Color` / `Color32` | Color            | RGBA (float or byte)                   |
| `string`            | String           | Int32 length + UTF-8 bytes + alignment |

### Build Commands

```powershell
# Full build
dotnet build AssetStudio.sln -c Release

# CLI only
dotnet build AssetStudio.CLI/AssetStudio.CLI.csproj -c Release

# GUI only
dotnet build AssetStudio.GUI/AssetStudio.GUI.csproj -c Release

# Linux GUI (run) and Linux release packages
dotnet run --project AssetStudio.Avalonia
./build-linux.sh && ./build-packages.sh

# Clean
dotnet clean AssetStudio.sln
```

### Release Process

1. Update `VERSION` file
2. Commit changes: `git commit -m "v2.3.2 - Description"`
3. Tag: `git tag v2.3.2`
4. Push: `git push origin main --tags`
5. GitHub Actions builds and publishes automatically

---

## Additional Resources

### Unity Documentation

- [Unity Serialization](https://docs.unity3d.com/Manual/script-Serialization.html)
- [Unity Asset Bundles](https://docs.unity3d.com/Manual/AssetBundlesIntro.html)

### Reverse Engineering

- [Unity Asset Bundle Extractor (UABE)](https://github.com/SeriousCache/UABE)
- [Unity Version Archive](https://unity.com/releases/editor/archive)

### Community

- Check GitHub Issues for known problems
- Review closed PRs for similar changes
- Look at commit history for context on complex changes

---

## Final Notes for AI Assistants

**When working on this project:**

1. **Always check Unity version context** - Format differences are version-dependent
2. **Prioritize graceful degradation** - Partial data > crashes
3. **Test with real game files** - Unity Editor exports differ from real games
4. **Read existing code patterns** - This codebase has established conventions
5. **Consider thread safety** - Multi-threading is everywhere
6. **Log verbosely** - Debugging binary formats requires detailed logs
7. **Be patient with mysteries** - Unity doesn't document everything

**This is a reverse engineering project** - Sometimes you'll encounter:

- Undocumented format changes
- Game-specific encryption/obfuscation
- Proprietary Unity modifications
- Trial-and-error debugging

**The goal**: Make game asset extraction possible even when Unity changes formats unexpectedly.

---

**Questions? Check:**

1. This document
2. `README.md` (user-facing features)
3. `RELEASE.md` (version/release process)
4. Code comments in complex classes (Shader.cs, AssetsManager.cs)
5. Git history for similar changes

Good luck! 🎮
