#!/usr/bin/env bash
# Builds libspirv-cross-c-shared.so (SPIR-V -> Vulkan GLSL, used by the shader exporter on Linux).
#
# Requirements: a C++ compiler, cmake, curl.
#
#   ./build-spirvcross-linux.sh
#   ARCH=arm64 ./build-spirvcross-linux.sh   # cross compile with aarch64-linux-gnu-g++
#
# Optional: ARCH (x64 or arm64, default x64), HOST (GNU triplet of a cross compiler; picked automatically when
# ARCH is not the build machine's), CC / CXX / STRIP (use an old glibc sysroot for maximum compatibility),
# SPIRV_CROSS_TAG, OUT (default AssetStudio.Avalonia/Libraries/$ARCH). libstdc++ is linked statically.
# SPIRV-Cross is Apache-2.0 (https://github.com/KhronosGroup/SPIRV-Cross).
set -euo pipefail

cd "$(dirname "$0")"
ROOT="$PWD"
ARCH="${ARCH:-x64}"
case "$ARCH" in
  x64) TRIPLET=x86_64-linux-gnu ;;
  arm64) TRIPLET=aarch64-linux-gnu ;;
  *) echo "unknown ARCH '$ARCH' (x64 or arm64)" >&2; exit 1 ;;
esac
CROSS=()
[ "${TRIPLET%%-*}" = "$(uname -m)" ] || HOST="${HOST:-$TRIPLET}"
if [ -n "${HOST:-}" ]; then
  export CC="${CC:-$HOST-gcc}" CXX="${CXX:-$HOST-g++}"
  STRIP="${STRIP:-$HOST-strip}"
  CROSS=(-DCMAKE_SYSTEM_NAME=Linux -DCMAKE_SYSTEM_PROCESSOR="${HOST%%-*}")
fi
SPIRV_CROSS_TAG="${SPIRV_CROSS_TAG:-vulkan-sdk-1.4.357.0}"
OUT="${OUT:-$ROOT/AssetStudio.Avalonia/Libraries/$ARCH}"
WORK="${WORK:-$ROOT/build/spirv-cross}"

mkdir -p "$WORK" "$OUT"
cd "$WORK"

if [ ! -d "SPIRV-Cross-${SPIRV_CROSS_TAG}" ]; then
  curl -fsSL -o spirv-cross.tar.gz "https://github.com/KhronosGroup/SPIRV-Cross/archive/refs/tags/${SPIRV_CROSS_TAG}.tar.gz"
  tar xzf spirv-cross.tar.gz
fi

cmake -S "SPIRV-Cross-${SPIRV_CROSS_TAG}" -B "build-$ARCH" -DCMAKE_BUILD_TYPE=Release "${CROSS[@]}" \
  -DSPIRV_CROSS_SHARED=ON -DSPIRV_CROSS_STATIC=OFF -DSPIRV_CROSS_CLI=OFF -DSPIRV_CROSS_ENABLE_TESTS=OFF \
  -DSPIRV_CROSS_ENABLE_GLSL=ON -DSPIRV_CROSS_ENABLE_HLSL=OFF -DSPIRV_CROSS_ENABLE_MSL=OFF -DSPIRV_CROSS_ENABLE_CPP=OFF \
  -DSPIRV_CROSS_ENABLE_REFLECT=OFF -DSPIRV_CROSS_ENABLE_UTIL=OFF \
  -DCMAKE_SHARED_LINKER_FLAGS="-static-libstdc++ -static-libgcc"
cmake --build "build-$ARCH" -j"$(nproc)"

cp -L "build-$ARCH/libspirv-cross-c-shared.so" "$OUT/libspirv-cross-c-shared.so"
"${STRIP:-strip}" "$OUT/libspirv-cross-c-shared.so" 2>/dev/null || true
echo "Done: $OUT/libspirv-cross-c-shared.so"
