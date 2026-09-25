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
