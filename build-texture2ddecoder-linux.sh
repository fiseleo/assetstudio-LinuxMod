#!/usr/bin/env bash
# Builds libTexture2DDecoderNative.so (texture decoding) for architectures the Kyaru.Texture2DDecoder.Linux
# NuGet package has no binary for (it only ships linux-x64 and linux-x86), i.e. linux-arm64.
#
# Requirements: a C++ compiler, cmake, curl.
#
#   ARCH=arm64 ./build-texture2ddecoder-linux.sh
#
# Optional: ARCH (x64 or arm64, default arm64), HOST (GNU triplet of a cross compiler, e.g. aarch64-linux-gnu;
# picked automatically when ARCH is not the build machine's), CC / CXX / STRIP (use an old glibc sysroot for
# maximum compatibility), T2D_COMMIT (must match the native API of the Kyaru.Texture2DDecoder wrapper),
# OUT (default AssetStudio.Avalonia/Libraries/$ARCH). libstdc++ is linked statically (and not re-exported).
# Texture2DDecoder is MIT (https://github.com/KiruyaMomochi/Texture2DDecoder).
set -euo pipefail

cd "$(dirname "$0")"
ROOT="$PWD"
ARCH="${ARCH:-arm64}"
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
T2D_COMMIT="${T2D_COMMIT:-ad5e7257d76542db17b6db7eff0c6d44b616c55d}"  # Kyaru.Texture2DDecoder 0.17.0
OUT="${OUT:-$ROOT/AssetStudio.Avalonia/Libraries/$ARCH}"
WORK="${WORK:-$ROOT/build/texture2ddecoder}"

mkdir -p "$WORK" "$OUT"
cd "$WORK"

if [ ! -d "Texture2DDecoder-${T2D_COMMIT}" ]; then
  curl -fsSL -o texture2ddecoder.tar.gz "https://github.com/KiruyaMomochi/Texture2DDecoder/archive/${T2D_COMMIT}.tar.gz"
  tar xzf texture2ddecoder.tar.gz
fi

# -fsigned-char: the decoders were written for x86, where char is signed (it is unsigned on arm64)
cmake -S "Texture2DDecoder-${T2D_COMMIT}/Texture2DDecoderNative" -B "build-$ARCH" -DCMAKE_BUILD_TYPE=Release "${CROSS[@]}" \
  -DCMAKE_CXX_FLAGS="-fsigned-char" -DCMAKE_SHARED_LINKER_FLAGS="-static-libstdc++ -static-libgcc -Wl,--exclude-libs,ALL"
cmake --build "build-$ARCH" -j"$(nproc)"

cp -L "build-$ARCH/libTexture2DDecoderNative.so" "$OUT/libTexture2DDecoderNative.so"
"${STRIP:-strip}" "$OUT/libTexture2DDecoderNative.so" 2>/dev/null || true
echo "Done: $OUT/libTexture2DDecoderNative.so"
