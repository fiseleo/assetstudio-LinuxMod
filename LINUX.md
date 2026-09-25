# AssetStudio on Linux

The Windows GUI (`AssetStudio.GUI`) is WinForms and only runs on Windows. Linux uses
**`AssetStudio.Avalonia`**, a cross-platform GUI built with [Avalonia](https://avaloniaui.net/)
on top of the same core libraries (`AssetStudio`, `AssetStudio.Utility`). The CLI also builds for Linux.

## Quick start

Requirements for building: .NET 8 SDK (`dotnet --list-sdks`).

```bash
# run from source
dotnet run --project AssetStudio.Avalonia

# open files / folders directly
dotnet run --project AssetStudio.Avalonia -- /path/to/game/Data

# self-contained release (no .NET needed on the target machine)
./build-linux.sh                      # -> dist/AssetStudio-linux-x64 (+ .tar.gz)
RID=linux-arm64 ./build-linux.sh      # other architectures
SELF_CONTAINED=false ./build-linux.sh # smaller, needs the .NET 8 runtime

# add a launcher to the application menu (run inside the release folder)
./install-desktop-entry.sh
```

The release folder contains both `AssetStudio.Avalonia` (GUI) and `AssetStudio.CLI`.

> Note: `dotnet build AssetStudio.sln` on Linux also tries to build the WinForms project.
> Build the individual projects instead (`AssetStudio.Avalonia`, `AssetStudio.CLI`), or use `build-linux.sh`.

## Features

Same layout and menus as the Windows GUI:

- Load files / folders (menu, drag & drop, or command line arguments), extract bundles
- Scene Hierarchy (checkboxes, regex search: `Enter` next match, `Shift` all, `Ctrl` check, `Alt` root),
  Asset List (regex filter, column sort, type filter, `Ctrl+A`, context menu), Asset Classes
- Preview: Texture2D / Sprite (channel toggle `Ctrl+R/G/B/A`), text / shader / MonoBehaviour / AnimationClip,
  fonts, audio info + playback, mesh and model 3D preview (Vulkan GPU renderer, see below: left drag rotate,
  right drag pan, wheel zoom, `Ctrl+W` wireframe), Dump tab
- Export: Convert / Raw / Dump / JSON for all, selected or filtered assets, asset list XML,
  scene hierarchy JSON, class structures
- Game selection, Unity version, UnityCN keys, AI versions, CABMap / AssetMap building (Misc. menu)
- Export options dialog (same settings as Windows)

Settings are stored in `~/.config/AssetStudio/settings.json`. The log is shown in the panel at the
bottom right and written to stdout (Debug menu). `Keys.json` and `Maps/` live next to the executable, as on Windows.

## Native libraries

| Library | Used for | Linux status |
|---------|----------|--------------|
| Texture2DDecoderNative | texture decoding (DXT, ETC, ASTC, Crunch, ...) | included (`Kyaru.Texture2DDecoder.Linux`) |
| FMOD (`libfmod.so`) | AudioClip → WAV | optional. Without FMOD, Unity 5+ audio (FSB5) is decoded with the managed [Fmod5Sharp](https://github.com/SamboyCoding/Fmod5Sharp): Vorbis → `.ogg`, PCM/ADPCM → `.wav` |
| AssetStudio.FBXNative | FBX export (Model menu, Animator, GameObject) | included (`AssetStudio.Avalonia/Libraries/x64/libAssetStudio.FBXNative.so`, needs glibc ≥ 2.28) |
| HLSLDecompiler / d3dcompiler | DirectX shader decompile | Windows only. On Linux DirectX programs go through Vulkan instead (next rows) |
| vkd3d-shader (`libvkd3d-shader.so`) | DirectX shader (SM2/3 bytecode, SM4/5 DXBC) → Vulkan SPIR-V | included (vkd3d 1.19, LGPL-2.1, rebuilt via `build-vkd3d-linux.sh`) |
| SPIRV-Cross (`libspirv-cross-c-shared.so`) | SPIR-V → Vulkan GLSL (readable shader code) | included (Apache-2.0, rebuilt via `build-spirvcross-linux.sh`). Without it the SPIR-V disassembly is written instead |
| Vulkan loader + driver (`libvulkan.so.1`) | GPU mesh / model preview | system (e.g. `libvulkan1` + the GPU driver's Vulkan ICD) |

Optional libraries are loaded from the `x64/` (or `arm64/`) folder next to the executable using Linux names:

- FMOD: download the *FMOD Engine* for Linux from fmod.com (free account needed) and copy
  `api/core/lib/x86_64/libfmod.so*` to `x64/libfmod.so`.
- FBX: a prebuilt `libAssetStudio.FBXNative.so` ships in `x64/`. It is statically linked (FBX SDK 2020.3.7,
  libxml2, zlib, libstdc++) and only depends on glibc 2.28+. To rebuild it (e.g. after changing the C++ code),
  install the Autodesk FBX SDK for Linux (accepting its license) and run:
  ```bash
  FBXSDK_ROOT=/path/to/fbxsdk ./build-fbxnative-linux.sh   # -> AssetStudio.Avalonia/Libraries/x64/
  ```
  Build with an old glibc sysroot (e.g. conda-forge `gxx_linux-64=13 sysroot_linux-64=2.28`, via `CC`/`CXX`)
  to keep the glibc requirement low. FBX exports run one at a time: the FBX SDK is not thread-safe.

### Vulkan

- **Model preview**: meshes and models are rendered on the GPU with Vulkan (offscreen, 4x MSAA, smooth shading with the
  mesh normals). The device is picked automatically (discrete GPU > integrated > software lavapipe) and shown in the
  status bar / log. Without a usable Vulkan driver the built-in software renderer is used; force it with
  `ASSETSTUDIO_RENDERER=software`. The preview shaders are in `AssetStudio.Avalonia/Shaders` (GLSL, embedded as SPIR-V;
  rebuild with `Shaders/compile.sh`, needs `glslc`).
- **Shaders**: on Linux, DirectX programs in exported `.shader` files are translated DirectX → Vulkan SPIR-V
  (vkd3d-shader) → Vulkan GLSL (SPIRV-Cross), replacing the Windows-only HLSL decompiler. If that fails the vkd3d
  Direct3D assembly listing is written instead. Programs that are already Vulkan (SPIR-V) are handled as before.

Audio playback in the preview uses `pw-play`, `paplay` or `ffplay` (whichever is installed).

## 中文說明

- Linux 版 GUI 是 `AssetStudio.Avalonia`（Avalonia 跨平台介面），功能與選單與 Windows 版相同。
- 執行：`dotnet run --project AssetStudio.Avalonia`；打包：`./build-linux.sh`，產生 `dist/AssetStudio-linux-x64`（含 GUI 與 CLI，不需安裝 .NET）。
- 模型預覽使用 Vulkan GPU 算繪（自動選擇顯示卡，狀態列會顯示；沒有 Vulkan 時改用軟體算繪，可用 `ASSETSTUDIO_RENDERER=software` 強制）。
- Shader 匯出：DirectX 程式在 Linux 上改走 Vulkan：vkd3d-shader 轉成 SPIR-V，再由 SPIRV-Cross 反編譯成 Vulkan GLSL（取代 Windows 專用的 HLSL 反編譯器）。
- 貼圖解碼、FBX 匯出（`x64/libAssetStudio.FBXNative.so`，需 glibc 2.28 以上）都已內建；音訊沒有 FMOD 時會用 Fmod5Sharp 轉成 `.ogg`/`.wav`。
- 重新編譯 FBX 原生庫：安裝 Linux 版 Autodesk FBX SDK 後執行 `FBXSDK_ROOT=... ./build-fbxnative-linux.sh`。
- 設定檔位於 `~/.config/AssetStudio/settings.json`。
