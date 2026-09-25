#!/usr/bin/env bash
# Build the Linux release: Avalonia GUI + CLI, self-contained (no .NET install needed to run).
#
#   ./build-linux.sh                 # linux-x64 -> dist/AssetStudio-linux-x64
#   RID=linux-arm64 ./build-linux.sh # other runtime identifiers
#   SELF_CONTAINED=false ./build-linux.sh  # smaller, needs the .NET 8 runtime installed
set -euo pipefail

cd "$(dirname "$0")"
RID="${RID:-linux-x64}"
SELF_CONTAINED="${SELF_CONTAINED:-true}"
OUT="dist/AssetStudio-${RID}"

rm -rf "$OUT"
dotnet publish AssetStudio.Avalonia/AssetStudio.Avalonia.csproj -c Release -r "$RID" --self-contained "$SELF_CONTAINED" -o "$OUT"
dotnet publish AssetStudio.CLI/AssetStudio.CLI.csproj -c Release -f net8.0 -r "$RID" --self-contained "$SELF_CONTAINED" -o "$OUT"

# native libraries (FBX exporter, vkd3d-shader, SPIRV-Cross; optional FMOD) go here, see LINUX.md
mkdir -p "$OUT/x64"
cp AssetStudio.Avalonia/as.ico "$OUT/"
cp linux/assetstudio.desktop linux/install-desktop-entry.sh "$OUT/"
chmod +x "$OUT/AssetStudio.Avalonia" "$OUT/AssetStudio.CLI" "$OUT/install-desktop-entry.sh"
find "$OUT" -name '*.pdb' -delete

tar -C dist -czf "dist/AssetStudio-${RID}.tar.gz" "AssetStudio-${RID}"
echo "Done: $OUT  (archive: dist/AssetStudio-${RID}.tar.gz)"
