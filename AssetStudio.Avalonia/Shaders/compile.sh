#!/usr/bin/env bash
# Recompiles the preview shaders to SPIR-V (the .spv files are embedded into AssetStudio.Avalonia).
# Needs glslc (shaderc), e.g. from the Vulkan SDK, the "shaderc" distro package or conda-forge.
set -euo pipefail
cd "$(dirname "$0")"
GLSLC="${GLSLC:-glslc}"
for f in *.vert *.frag; do
    "$GLSLC" -O --target-env=vulkan1.0 "$f" -o "$f.spv"
done
