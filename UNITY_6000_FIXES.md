# Unity 6000 (6000.x) Compatibility Fixes

## Overview

This document describes the changes made to support Unity 6000 (6000.x series) asset files, specifically addressing shader parsing failures that occurred due to format changes introduced in Unity 6000.

## Problem Description

Unity 6000.0.58f2 introduced **undocumented changes** to the Shader serialization format:

### Symptoms

- **1,082 shaders** failed to fully parse with error: `Unable to read beyond the end of the stream`
- Massive TypeTree string read errors (21,989+) showing pattern `String length 1751347809 (0x68637261) exceeds remaining bytes`
- The hex value `0x68637261` = ASCII "arch", indicating byte misalignment causing shader bytecode to be interpreted as string length values

### Root Cause

Unity 6000 **removed** the following fields from `SerializedPass` structure:

- `m_EditorDataHash` (List<Hash128>)
- `m_Platforms` (byte[])
- `m_LocalKeywordMask` (ushort[]) [for versions < 2021.2]
- `m_GlobalKeywordMask` (ushort[]) [for versions < 2021.2]

These fields were present in Unity 2020.2 through Unity 2021.x but are no longer serialized in Unity 6000.

## Solution Implementation

### File: `AssetStudio/Classes/Shader.cs`

**Location:** Lines 862-863 in `SerializedPass` constructor

**Before:**

```csharp
if (version[0] > 2020 || (version[0] == 2020 && version[1] >= 2)) //2020.2 and up
{
    // Read m_EditorDataHash, m_Platforms, m_LocalKeywordMask, m_GlobalKeywordMask
}
```

**After:**

```csharp
// Unity 6000 removed EditorDataHash and Platforms fields
if ((version[0] > 2020 || (version[0] == 2020 && version[1] >= 2)) && version[0] < 6000) //2020.2 to 2021.x
{
    // Read m_EditorDataHash, m_Platforms, m_LocalKeywordMask, m_GlobalKeywordMask
}
```

### Key Change

Added `&& version[0] < 6000` condition to **explicitly exclude Unity 6000** from reading the removed fields.

## Research Sources

This fix was derived from analyzing the AXiX-official/Studio fork:

- Repository: https://github.com/AXiX-official/Studio
- Key file: `AssetStudio/Classes/Shader.cs` line 856
- Their implementation: `if (version >= "2020.2" && version < "6000")`

## Testing Results

### Build Status

✅ **Build succeeded** with 108 warnings (all pre-existing dependency vulnerabilities, not related to this fix)

### Expected Impact

- **Shader parsing**: All 1,082 failing shaders should now parse correctly
- **TypeTree errors**: The 21,989+ string length errors should be eliminated or drastically reduced (these were caused by byte misalignment from incorrectly reading the removed fields)
- **Asset decoding**: MonoBehaviour and other assets dependent on correct byte alignment should now deserialize properly

## Version Detection Context

The codebase uses `int[] version` where:

- `version[0]` = Major version (e.g., 2020, 2021, 6000)
- `version[1]` = Minor version (e.g., 2 for Unity 2020.2)

Unity 6000 series uses **6000** as the major version number, not "6.0".

## Additional Notes

### Debug Logging

The existing v2.3.1 codebase already includes comprehensive DEBUG-level logging:

- Shader parse failures with position/size diagnostics
- TypeTree string reads with hex dumps
- Try-catch error handling across 4 constructor layers

### Alternative Forks Analyzed

- **Modder4869/StudioDev**: Suspended, no Unity 6000 specific fixes found
- **hashblen/ZZZ_Studio**: Similar structure, no complete Unity 6000 shader fix
- **AXiX-official/Studio**: ✅ Contains the version exclusion fix used here

## Related Issues

This fix addresses the primary concern mentioned in user logs:

- Shader parse failures: `Failed to parse SerializedSubShader (Unity 6000.0)`
- TypeTree deserialization errors when byte stream position is incorrect

## Future Considerations

Unity 6000 may have additional format changes beyond shaders. Monitor for:

- Other asset types with parsing issues
- New Unity 6000-specific features requiring format updates
- Continued evolution in Unity 6.x series requiring additional version-specific handling

## Shader parsing and export (Linux port, 2026-09)

Shader parsing had been skipped for Unity 6000 (the whole object was read as raw bytes), so shader export produced
nothing. Checked against the TypeTree of a 6000.0.65f1 player (UnityDataTools `PlayerWithTypeTrees`):

- `SerializedPass`: `m_EditorDataHash` / `m_Platforms` (and the keyword masks) are not serialized in Unity 6.
- `SerializedProgram.m_PlayerSubPrograms` / `m_ParameterBlobIndices` and `Shader.stageCounts` exist in 2021.3.10+,
  2022.1.13+ **and every later version**; the version checks only listed 2021/2022, so 2023 and Unity 6 missed them.
- Everything else matches 2022.3.

Export: player builds of 2021.3.10+ / 2022.1.13+ / Unity 6 keep the subprograms in `m_PlayerSubPrograms`
(`m_SubPrograms` is empty), which the converter ignored, so their shaders had no programs. They are exported now.
The compressed blob of these versions also holds parameter entries (referenced by `m_ParameterBlobIndices`),
which are skipped instead of being parsed as programs.

## Unity 2023.1 – 6000.7 class layouts (Linux port, 2026-09)

Each change was located with the release type trees of every Unity version in
[AssetRipper/TypeTreeDumps](https://github.com/AssetRipper/TypeTreeDumps) (bisected to the exact alpha/beta), and
checked against real files with type trees by comparing the manual parsers with the type tree values (UnityDataTools
test data: 2019.4, 2022.1, 2023.1.0a16, 6000.0.65, 6000.6.0b3, 6000.7.0b2 players and bundles).
`ObjectReader.IsVersionAtLeast` compares the full version including the release stage, since several layouts changed
in the middle of an alpha cycle.

| Since | Class | Change |
|-------|-------|--------|
| 2023.1.0a9 | Shader | `SerializedPass.m_EditorDataHash` / `m_Platforms` removed |
| 2023.2.0a13 | Renderer | `m_RayTracingAccelStructBuildFlagsOverride`, `m_RayTracingAccelStructBuildFlags` |
| 2023.3.0a16 (6000.0) | Renderer | `m_SmallMeshCulling` |
| 6000.2.0a8 | Renderer | `m_ForceMeshLod`, `m_MeshLodSelectionBias` (Mesh `m_MeshLodInfo` at the end, not read) |
| 6000.2.0b2 | AnimatorController | `ValueArray.m_EntityIdValues` |
| 6000.3.0a5 | Renderer | `m_MaskInteraction` |
| 6000.4.0a2 | AnimatorController | `m_EvaluateTransitionsOnStart` |
| 6000.5.0a3 | Sprite | `m_IsPolygon` removed |
| 6000.5.0a7 | Sprite / AnimationClip | `SpriteRenderData.m_BlendShapes`; `GenericBinding.metaData` |
| 6000.6.0a2 | Shader | parameter indices became `Binding` structs (8 bytes) |
| 6000.6.0a3 | SpriteAtlas | `m_PackedSprites` / `m_PackedSpriteNamesToIndex` removed, `SpriteAtlasData.spriteInstanceData` added; sprites are matched to atlases by render data key |
| 6000.6.0a5 | Mesh | `m_MeshUsageFlags` replaced by `m_PreBake*CollisionMesh` |
| 6000.6.0a7 | Shader | `BufferBinding.m_ResourceType` |
| 6000.7.0a2 | Shader | `ProgramParameters.m_SpecializationConstantParams` replaces the vector / matrix parameters |
| 6000.7.0a3 | Shader | `SerializedPass.m_SerializedDynamicBranchKeywordMask` |

SerializedFile formats: 23 (6000.5, type tree blobs, optionally extracted to a `.typetreedata` file) and 26 (6000.7,
shared sub trees) are read.

Shader platform 28 is D3D12 (6000.7, `ShaderCompilerPlatform.D3D12`, compiled with dxcompiler). Its GPU program types
are not public (34 = vertex and 37 = pixel in the UnityDataTools 6000.7 player); every program is a DXBC container
behind a small header, holding DXIL or SM4/5 byte code, and is exported like a D3D11 program (DXIL goes through
vkd3d-shader's DXIL front end). WebGPU (26) and Switch2 (27) programs are still skipped.
