#!/usr/bin/env bash
# Builds libvkd3d-shader.so (DirectX shader byte code -> Vulkan SPIR-V, used by the shader exporter on Linux).
#
# Requirements: a C compiler, make, flex, bison, perl (with JSON or JSON::PP), curl, pkg-config,
# SPIRV-Headers and Vulkan-Headers (e.g. distro packages spirv-headers + libvulkan-dev).
#
#   ./build-vkd3d-linux.sh
#
# Optional: CC (use an old glibc sysroot for maximum compatibility), CPPFLAGS (extra include dirs for the
# headers), VKD3D_VERSION, OUT (default AssetStudio.Avalonia/Libraries/x64).
# vkd3d is LGPL-2.1 (https://gitlab.winehq.org/wine/vkd3d); it is loaded dynamically. linux/vkd3d-*.patch are
# applied to it (AssetStudio changes, same license).
set -euo pipefail

cd "$(dirname "$0")"
ROOT="$PWD"
VKD3D_VERSION="${VKD3D_VERSION:-2.1}"
OUT="${OUT:-$ROOT/AssetStudio.Avalonia/Libraries/x64}"
WORK="${WORK:-$ROOT/build/vkd3d}"

mkdir -p "$WORK" "$OUT"
cd "$WORK"

if [ ! -d "vkd3d-${VKD3D_VERSION}" ]; then
  curl -fsSL -o vkd3d.tar.xz "https://dl.winehq.org/vkd3d/source/vkd3d-${VKD3D_VERSION}.tar.xz"
  tar xJf vkd3d.tar.xz
fi
cd "vkd3d-${VKD3D_VERSION}"

# D3D9 instructions the SPIR-V backend does not handle (lit, dst, crs, sgn, expp, logp), see the patch
if [ ! -f .assetstudio-patched ]; then
  for p in "$ROOT"/linux/vkd3d-*.patch; do
    patch -p1 < "$p"
  done
  touch .assetstudio-patched
fi

# configure insists on perl's JSON module; JSON::PP (core perl) is enough
if ! perl -MJSON -e 1 2>/dev/null; then
  mkdir -p "$WORK/perl5"
  printf 'package JSON;\nuse parent "JSON::PP";\nour @EXPORT = @JSON::PP::EXPORT;\nsub import { JSON::PP->export_to_level(1, @_); }\n1;\n' > "$WORK/perl5/JSON.pm"
  export PERL5LIB="$WORK/perl5${PERL5LIB:+:$PERL5LIB}"
fi

if [ ! -f Makefile ]; then
  # the Metal (Objective-C) automake conditional is only defined on macOS
  sed -i 's/^if test -z "\${am__fastdepOBJC_TRUE}" && test -z "\${am__fastdepOBJC_FALSE}"; then/if false; then/' configure
  ./configure --disable-tests --disable-doxygen-doc --without-ncurses --without-xcb --without-opengl --without-metal \
    CFLAGS="${CFLAGS:--O2 -g0}" SONAME_LIBVULKAN=libvulkan.so.1  # only libvkd3d (not built here) links Vulkan
fi
make include/private/vkd3d_version.h libs/vkd3d-shader/hlsl.tab.h
make -j"$(nproc)" libvkd3d-shader.la

cp -L .libs/libvkd3d-shader.so "$OUT/libvkd3d-shader.so"
strip "$OUT/libvkd3d-shader.so" 2>/dev/null || true
echo "Done: $OUT/libvkd3d-shader.so"
