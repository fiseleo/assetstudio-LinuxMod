#!/usr/bin/env bash
# Builds libglslang.so (GLSL -> SPIR-V through glslang's C interface: the game shader preview of the OpenGL ES
# programs) with only the glslang_* functions exported.
#
# Requirements: a C++ compiler, cmake, python3, curl.
#
#   ./build-glslang-linux.sh
#
# Optional: CC / CXX (use an old glibc sysroot for maximum compatibility), GLSLANG_TAG,
# OUT (default AssetStudio.Avalonia/Libraries/x64). libstdc++ is linked statically.
# glslang is BSD-3-Clause / BSD-2-Clause / MIT / Apache-2.0, its generated grammar GPL-3.0 with the Bison exception
# (https://github.com/KhronosGroup/glslang, see LICENSE.txt there).
set -euo pipefail

cd "$(dirname "$0")"
ROOT="$PWD"
GLSLANG_TAG="${GLSLANG_TAG:-16.6.0}"
OUT="${OUT:-$ROOT/AssetStudio.Avalonia/Libraries/x64}"
WORK="${WORK:-$ROOT/build/glslang}"
CXX="${CXX:-c++}"

mkdir -p "$WORK" "$OUT"
cd "$WORK"

if [ ! -d "glslang-${GLSLANG_TAG}" ]; then
  curl -fsSL -o glslang.tar.gz "https://github.com/KhronosGroup/glslang/archive/refs/tags/${GLSLANG_TAG}.tar.gz"
  tar xzf glslang.tar.gz
fi

cmake -S "glslang-${GLSLANG_TAG}" -B build -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=OFF \
  -DCMAKE_POSITION_INDEPENDENT_CODE=ON -DENABLE_OPT=OFF -DENABLE_HLSL=OFF -DENABLE_GLSLANG_BINARIES=OFF \
  -DGLSLANG_TESTS=OFF -DGLSLANG_ENABLE_INSTALL=OFF -DENABLE_SPVREMAPPER=OFF
cmake --build build -j"$(nproc)"

# one shared library of the static ones, exporting the C interface only
cat > exports.map <<'EOF'
{
  global: glslang_*;
  local: *;
};
EOF
# (GenericCodeGen, MachineIndependent, OSDependent and SPIRV are stubs now, their code is in libglslang.a)
ARCHIVES=(build/glslang/libglslang.a build/glslang/libglslang-default-resource-limits.a)
"$CXX" -shared -o "$OUT/libglslang.so" -Wl,--whole-archive "${ARCHIVES[@]}" -Wl,--no-whole-archive \
  -Wl,--version-script=exports.map -static-libstdc++ -static-libgcc -lpthread
strip "$OUT/libglslang.so" 2>/dev/null || true
echo "Done: $OUT/libglslang.so"
