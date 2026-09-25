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

# packages (from the release folder above, built first if missing)
./build-packages.sh                   # -> dist/AssetStudio-x86_64.AppImage + dist/assetstudio_<version>_amd64.deb
```

- **AppImage**: one file, no installation: `chmod +x AssetStudio-x86_64.AppImage && ./AssetStudio-x86_64.AppImage`.
  The command line version is inside too: `./AssetStudio-x86_64.AppImage cli <input> <output> --game Normal`.
- **.deb** (Debian / Ubuntu): `sudo apt install ./assetstudio_<version>_amd64.deb` installs to `/opt/assetstudio`,
  with `assetstudio` / `assetstudio-cli` commands and a menu entry.

The release folder contains both `AssetStudio.Avalonia` (GUI) and `AssetStudio.CLI`.

> Note: `dotnet build AssetStudio.sln` works on Linux too. The WinForms project (`AssetStudio.GUI`) is only
> compiled there (to catch build breaks), it cannot run. Use `AssetStudio.Avalonia` or `build-linux.sh` to run.

## Features

Same layout and menus as the Windows GUI:

- Load files / folders (menu, drag & drop, or command line arguments), extract bundles
- Scene Hierarchy (checkboxes, regex search: `Enter` next match, `Shift` all, `Ctrl` check, `Alt` root),
  Asset List (regex filter, column sort, type filter, `Ctrl+A`, context menu), Asset Classes
- Preview: Texture2D / Sprite (channel toggle `Ctrl+R/G/B/A`; wheel zoom, drag to pan, double-click = fit / 100%), text / shader / MonoBehaviour / AnimationClip,
  fonts, audio info + playback (pause, loop, seek, volume), mesh and model 3D preview (Vulkan GPU renderer, see below: left drag rotate,
  right drag pan, wheel zoom, `Ctrl+W` wireframe), Dump tab
- Export: Convert / Raw / Dump / JSON for all, selected or filtered assets, asset list XML,
  scene hierarchy JSON, class structures
- Game selection, Unity version, UnityCN keys, AI versions, CABMap / AssetMap building (Misc. menu)
- Asset Browser (Misc. menu): open an AssetMap (`.map`), filter it by regex per column, then load the files
  of the selected entries or export just the selected assets (files are loaded one at a time)
- Export options dialog (same settings as Windows)

Settings are stored in `~/.config/AssetStudio/settings.json`. The log is shown in the panel at the
bottom right and written to stdout (Debug menu). `Keys.json`, `Maps/` and `log.txt` live next to the executable, as on
Windows; when that folder is read-only (AppImage, `.deb` install) they go to `~/.local/share/AssetStudio` instead.

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
  mesh normals, textured with each material's main texture, mipmapped). The device is picked automatically (discrete GPU > integrated > software lavapipe) and shown in the
  status bar / log. Without a usable Vulkan driver the built-in software renderer is used; force it with
  `ASSETSTUDIO_RENDERER=software`. The preview shaders are in `AssetStudio.Avalonia/Shaders` (GLSL, embedded as SPIR-V;
  rebuild with `Shaders/compile.sh`, needs `glslc`).
- **Shaders**: on Linux, DirectX programs in exported `.shader` files are translated DirectX → Vulkan SPIR-V
  (vkd3d-shader) → Vulkan GLSL (SPIRV-Cross), replacing the Windows-only HLSL decompiler. If that fails the vkd3d
  Direct3D assembly listing is written instead. Programs that are already Vulkan (SPIR-V) are handled as before.

Audio preview plays in-process through PulseAudio / PipeWire (`libpulse-simple.so.0`, present on almost every desktop)
with Play / Pause / Stop, Loop, a seek bar and volume, like the FMOD player on Windows. WAV and Ogg Vorbis are decoded
in-process ([NVorbis](https://github.com/NVorbis/NVorbis)). Without libpulse, `pw-play`, `paplay` or `ffplay` is used
instead (play / stop only).

## 中文說明

- Linux 版 GUI 是 `AssetStudio.Avalonia`（Avalonia 跨平台介面），功能與選單與 Windows 版相同。
- 執行：`dotnet run --project AssetStudio.Avalonia`；打包：`./build-linux.sh`，產生 `dist/AssetStudio-linux-x64`（含 GUI 與 CLI，不需安裝 .NET）。
- 模型預覽使用 Vulkan GPU 算繪，會貼上材質的主貼圖（自動選擇顯示卡，狀態列會顯示；沒有 Vulkan 時改用軟體算繪，可用 `ASSETSTUDIO_RENDERER=software` 強制）。
- Shader 匯出：DirectX 程式在 Linux 上改走 Vulkan：vkd3d-shader 轉成 SPIR-V，再由 SPIRV-Cross 反編譯成 Vulkan GLSL（取代 Windows 專用的 HLSL 反編譯器）。
- 貼圖解碼、FBX 匯出（`x64/libAssetStudio.FBXNative.so`，需 glibc 2.28 以上）都已內建；音訊沒有 FMOD 時會用 Fmod5Sharp 轉成 `.ogg`/`.wav`。
- 重新編譯 FBX 原生庫：安裝 Linux 版 Autodesk FBX SDK 後執行 `FBXSDK_ROOT=... ./build-fbxnative-linux.sh`。
- Asset Browser（Misc. 選單）：開啟 AssetMap（`.map`），各欄位可用正則篩選，可載入選取項目的檔案，或只匯出選取的資源。
- 音訊預覽直接透過 PulseAudio / PipeWire 播放，有播放、暫停、停止、循環、進度條拖曳和音量（和 Windows 版 FMOD 播放器相同）。
- 套件：`./build-packages.sh` 產生 AppImage（單一檔案，直接執行；`./AssetStudio-x86_64.AppImage cli ...` 為命令列版）與 `.deb`（`sudo apt install ./assetstudio_*.deb`，安裝到 `/opt/assetstudio`）。程式資料夾唯讀時，`Keys.json`、`Maps/` 與 log 會改存到 `~/.local/share/AssetStudio`。
- 設定檔位於 `~/.config/AssetStudio/settings.json`。
