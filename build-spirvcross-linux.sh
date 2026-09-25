#!/usr/bin/env bash
# Builds libspirv-cross-c-shared.so (SPIR-V -> Vulkan GLSL, used by the shader exporter on Linux).
#
# Requirements: a C++ compiler, cmake, curl.
#
#   ./build-spirvcross-linux.sh
#
# Optional: CC / CXX (use an old glibc sysroot for maximum compatibility), SPIRV_CROSS_TAG,
# OUT (default AssetStudio.Avalonia/Libraries/x64). libstdc++ is linked statically.
# SPIRV-Cross is Apache-2.0 (https://github.com/KhronosGroup/SPIRV-Cross).
set -euo pipefail

cd "$(dirname "$0")"
ROOT="$PWD"
SPIRV_CROSS_TAG="${SPIRV_CROSS_TAG:-vulkan-sdk-1.4.357.0}"
OUT="${OUT:-$ROOT/AssetStudio.Avalonia/Libraries/x64}"
WORK="${WORK:-$ROOT/build/spirv-cross}"

mkdir -p "$WORK" "$OUT"
cd "$WORK"

if [ ! -d "SPIRV-Cross-${SPIRV_CROSS_TAG}" ]; then
  curl -fsSL -o spirv-cross.tar.gz "https://github.com/KhronosGroup/SPIRV-Cross/archive/refs/tags/${SPIRV_CROSS_TAG}.tar.gz"
  tar xzf spirv-cross.tar.gz
fi

cmake -S "SPIRV-Cross-${SPIRV_CROSS_TAG}" -B build -DCMAKE_BUILD_TYPE=Release \
  -DSPIRV_CROSS_SHARED=ON -DSPIRV_CROSS_STATIC=OFF -DSPIRV_CROSS_CLI=OFF -DSPIRV_CROSS_ENABLE_TESTS=OFF \
  -DSPIRV_CROSS_ENABLE_GLSL=ON -DSPIRV_CROSS_ENABLE_HLSL=OFF -DSPIRV_CROSS_ENABLE_MSL=OFF -DSPIRV_CROSS_ENABLE_CPP=OFF \
  -DSPIRV_CROSS_ENABLE_REFLECT=OFF -DSPIRV_CROSS_ENABLE_UTIL=OFF \
  -DCMAKE_SHARED_LINKER_FLAGS="-static-libstdc++ -static-libgcc"
cmake --build build -j"$(nproc)"

cp -L build/libspirv-cross-c-shared.so "$OUT/libspirv-cross-c-shared.so"
strip "$OUT/libspirv-cross-c-shared.so" 2>/dev/null || true
echo "Done: $OUT/libspirv-cross-c-shared.so"
