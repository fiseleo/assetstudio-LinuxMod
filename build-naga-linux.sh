#!/usr/bin/env bash
# Builds libnaga_c.so (WGSL -> SPIR-V through naga: the game shader preview of the WebGPU programs) from
# linux/naga-c (a C interface over the naga crate).
#
# Requirements: Rust (cargo), a C compiler as the linker.
#
#   ./build-naga-linux.sh
#
# Optional: LINKER (e.g. a compiler with an old glibc sysroot for maximum compatibility), OUT (default
# AssetStudio.Avalonia/Libraries/x64). naga is MIT OR Apache-2.0 (https://github.com/gfx-rs/wgpu/tree/trunk/naga).
set -euo pipefail

cd "$(dirname "$0")"
ROOT="$PWD"
OUT="${OUT:-$ROOT/AssetStudio.Avalonia/Libraries/x64}"
export CARGO_TARGET_DIR="${CARGO_TARGET_DIR:-$ROOT/build/naga-c}"
if [ -n "${LINKER:-}" ]; then
  export CARGO_TARGET_X86_64_UNKNOWN_LINUX_GNU_LINKER="$LINKER"
fi

mkdir -p "$OUT"
cargo build --release --manifest-path linux/naga-c/Cargo.toml
cp "$CARGO_TARGET_DIR/release/libnaga_c.so" "$OUT/libnaga_c.so"
echo "Done: $OUT/libnaga_c.so"
