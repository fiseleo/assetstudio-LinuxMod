#!/usr/bin/env bash
# Builds libAssetStudio.FBXNative.so (FBX export for Linux).
#
# Requirements: a C/C++ compiler, cmake, curl, and the Autodesk FBX SDK for Linux
# (https://aps.autodesk.com/developer/overview/autodesk-fbx-sdk -> "FBX SDK 2020.3.x Linux",
#  run its installer and accept the license: ./fbx202037_fbxsdk_linux /path/to/fbxsdk).
#
#   FBXSDK_ROOT=/path/to/fbxsdk ./build-fbxnative-linux.sh
#
# Optional: CC / CXX to pick the compiler (use an old glibc sysroot for maximum compatibility),
# OUT (default AssetStudio.Avalonia/Libraries/x64). zlib and libxml2 are downloaded and linked
# statically, so the result only depends on glibc.
set -euo pipefail

cd "$(dirname "$0")"
ROOT="$PWD"
: "${FBXSDK_ROOT:?set FBXSDK_ROOT to the FBX SDK install directory}"
OUT="${OUT:-$ROOT/AssetStudio.Avalonia/Libraries/x64}"
WORK="${WORK:-$ROOT/build/fbxnative}"
ZLIB_VERSION=1.3.1
LIBXML2_VERSION=2.12.10

mkdir -p "$WORK" "$OUT"
cd "$WORK"
PREFIX="$WORK/prefix"

if [ ! -f "$PREFIX/lib/libz.a" ]; then
  curl -fsSL -o zlib.tar.gz "https://github.com/madler/zlib/releases/download/v${ZLIB_VERSION}/zlib-${ZLIB_VERSION}.tar.gz"
  tar xzf zlib.tar.gz
  cmake -S "zlib-${ZLIB_VERSION}" -B build-zlib -DCMAKE_BUILD_TYPE=Release -DCMAKE_POSITION_INDEPENDENT_CODE=ON \
    -DCMAKE_INSTALL_PREFIX="$PREFIX" -DZLIB_BUILD_EXAMPLES=OFF
  cmake --build build-zlib -j"$(nproc)"
  cmake --install build-zlib
fi

if [ ! -f "$PREFIX/lib/libxml2.a" ]; then
  curl -fsSL -o libxml2.tar.xz "https://download.gnome.org/sources/libxml2/${LIBXML2_VERSION%.*}/libxml2-${LIBXML2_VERSION}.tar.xz"
  tar xJf libxml2.tar.xz
  cmake -S "libxml2-${LIBXML2_VERSION}" -B build-xml -DCMAKE_BUILD_TYPE=Release -DCMAKE_POSITION_INDEPENDENT_CODE=ON \
    -DBUILD_SHARED_LIBS=OFF -DCMAKE_INSTALL_PREFIX="$PREFIX" \
    -DLIBXML2_WITH_PYTHON=OFF -DLIBXML2_WITH_LZMA=OFF -DLIBXML2_WITH_ICU=OFF -DLIBXML2_WITH_ZLIB=OFF \
    -DLIBXML2_WITH_TESTS=OFF -DLIBXML2_WITH_PROGRAMS=OFF -DLIBXML2_WITH_HTTP=OFF -DLIBXML2_WITH_FTP=OFF
  cmake --build build-xml -j"$(nproc)"
  cmake --install build-xml
fi

cmake -S "$ROOT/AssetStudio.FBXNative" -B build-fbx -DCMAKE_BUILD_TYPE=Release \
  -DFBXSDK_ROOT="$FBXSDK_ROOT" -DZLIB_STATIC="$PREFIX/lib/libz.a" -DLIBXML2_STATIC="$PREFIX/lib/libxml2.a"
cmake --build build-fbx -j"$(nproc)"

strip --strip-unneeded build-fbx/libAssetStudio.FBXNative.so
cp build-fbx/libAssetStudio.FBXNative.so "$OUT/"
echo "Built $OUT/libAssetStudio.FBXNative.so"
