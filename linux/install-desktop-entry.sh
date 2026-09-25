#!/usr/bin/env bash
# Adds AssetStudio to the application menu of the current user.
set -euo pipefail
DIR="$(cd "$(dirname "$0")" && pwd)"
TARGET="${XDG_DATA_HOME:-$HOME/.local/share}/applications/assetstudio.desktop"
mkdir -p "$(dirname "$TARGET")"
sed "s|@INSTALL_DIR@|$DIR|g" "$DIR/assetstudio.desktop" > "$TARGET"
chmod +x "$TARGET"
echo "Installed $TARGET"
