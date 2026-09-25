#!/usr/bin/env bash
# Builds libvkd3d-shader.so (DirectX shader byte code -> Vulkan SPIR-V, used by the shader exporter on Linux).
#
# Requirements: a C compiler, make, flex, bison, perl (with JSON or JSON::PP), curl, pkg-config,
# SPIRV-Headers and Vulkan-Headers (e.g. distro packages spirv-headers + libvulkan-dev).
#
#   ./build-vkd3d-linux.sh
#   ARCH=arm64 ./build-vkd3d-linux.sh   # cross compile with aarch64-linux-gnu-gcc
#
# Optional: ARCH (x64 or arm64, default x64), HOST (GNU triplet of a cross compiler; picked automatically when
# ARCH is not the build machine's), CC / STRIP (use an old glibc sysroot for maximum compatibility), CPPFLAGS
# (extra include dirs for the headers), VKD3D_VERSION, OUT (default AssetStudio.Avalonia/Libraries/$ARCH).
# vkd3d is LGPL-2.1 (https://gitlab.winehq.org/wine/vkd3d); it is loaded dynamically.
set -euo pipefail

cd "$(dirname "$0")"
ROOT="$PWD"
ARCH="${ARCH:-x64}"
case "$ARCH" in
  x64) TRIPLET=x86_64-linux-gnu ;;
  arm64) TRIPLET=aarch64-linux-gnu ;;
  *) echo "unknown ARCH '$ARCH' (x64 or arm64)" >&2; exit 1 ;;
esac
[ "${TRIPLET%%-*}" = "$(uname -m)" ] || HOST="${HOST:-$TRIPLET}"
if [ -n "${HOST:-}" ]; then
  export CC="${CC:-$HOST-gcc}"
  STRIP="${STRIP:-$HOST-strip}"
fi
VKD3D_VERSION="${VKD3D_VERSION:-1.19}"
OUT="${OUT:-$ROOT/AssetStudio.Avalonia/Libraries/$ARCH}"
WORK="${WORK:-$ROOT/build/vkd3d}"

mkdir -p "$WORK" "$OUT"
cd "$WORK"

if [ ! -d "vkd3d-${VKD3D_VERSION}" ]; then
  curl -fsSL -o vkd3d.tar.xz "https://dl.winehq.org/vkd3d/source/vkd3d-${VKD3D_VERSION}.tar.xz"
  tar xJf vkd3d.tar.xz
fi
SRC="$WORK/vkd3d-${VKD3D_VERSION}"

# configure insists on perl's JSON module; JSON::PP (core perl) is enough
if ! perl -MJSON -e 1 2>/dev/null; then
  mkdir -p "$WORK/perl5"
  printf 'package JSON;\nuse parent "JSON::PP";\nour @EXPORT = @JSON::PP::EXPORT;\nsub import { JSON::PP->export_to_level(1, @_); }\n1;\n' > "$WORK/perl5/JSON.pm"
  export PERL5LIB="$WORK/perl5${PERL5LIB:+:$PERL5LIB}"
fi

# the Metal (Objective-C) automake conditional is only defined on macOS
sed -i 's/^if test -z "\${am__fastdepOBJC_TRUE}" && test -z "\${am__fastdepOBJC_FALSE}"; then/if false; then/' "$SRC/configure"
mkdir -p "build-$ARCH"
cd "build-$ARCH"
if [ ! -f Makefile ]; then
  "$SRC/configure" ${HOST:+--host="$HOST"} --disable-tests --disable-doxygen-doc --without-ncurses --without-xcb --without-opengl --without-metal \
    CFLAGS="${CFLAGS:--O2 -g0}" SONAME_LIBVULKAN=libvulkan.so.1  # only libvkd3d (not built here) links Vulkan
fi
make include/private/vkd3d_version.h libs/vkd3d-shader/hlsl.tab.h
make -j"$(nproc)" libvkd3d-shader.la

cp -L .libs/libvkd3d-shader.so "$OUT/libvkd3d-shader.so"
"${STRIP:-strip}" "$OUT/libvkd3d-shader.so" 2>/dev/null || true
echo "Done: $OUT/libvkd3d-shader.so"
