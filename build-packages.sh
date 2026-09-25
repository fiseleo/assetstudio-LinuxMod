#!/usr/bin/env bash
# Package the Linux release built by build-linux.sh as an AppImage and a .deb.
#
#   ./build-packages.sh                   # linux-x64: dist/AssetStudio-x86_64.AppImage, dist/assetstudio_<ver>_amd64.deb
#   FORMATS=deb ./build-packages.sh       # only one format (appimage, deb)
#
# The release folder is built first when it does not exist. appimagetool is downloaded to build/ on first use.
set -euo pipefail

cd "$(dirname "$0")"
RID="${RID:-linux-x64}"
FORMATS="${FORMATS:-appimage deb}"
RELEASE="dist/AssetStudio-${RID}"
VERSION="$(sed -n 's|.*<Version>\(.*\)</Version>.*|\1|p' AssetStudio.Avalonia/AssetStudio.Avalonia.csproj | head -n1)"
VERSION="${VERSION:-0.0.0}"
case "$RID" in
    linux-x64) APPIMAGE_ARCH=x86_64; DEB_ARCH=amd64 ;;
    *) echo "unsupported RID $RID" >&2; exit 1 ;;
esac

[ -x "$RELEASE/AssetStudio.Avalonia" ] || RID="$RID" ./build-linux.sh

desktop_entry() { # $1 = Exec, $2 = Icon
    sed -e "s|^Exec=.*|Exec=$1 %F|" -e "s|^Icon=.*|Icon=$2|" linux/assetstudio.desktop
}

if [[ " $FORMATS " == *" appimage "* ]]; then
    APPDIR="build/AppDir-${RID}"
    rm -rf "$APPDIR"
    mkdir -p "$APPDIR/usr/lib/assetstudio"
    cp -a "$RELEASE/." "$APPDIR/usr/lib/assetstudio/"
    rm -f "$APPDIR/usr/lib/assetstudio/install-desktop-entry.sh" "$APPDIR/usr/lib/assetstudio/assetstudio.desktop"
    cp linux/AppRun "$APPDIR/AppRun"
    cp linux/assetstudio.png "$APPDIR/assetstudio.png"
    desktop_entry AssetStudio assetstudio > "$APPDIR/assetstudio.desktop"

    # the tool runs on the build machine, whatever the target architecture
    TOOL="build/appimagetool-$(uname -m).AppImage"
    if [ ! -x "$TOOL" ]; then
        mkdir -p build
        curl -fL -o "$TOOL" "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-$(uname -m).AppImage"
        chmod +x "$TOOL"
    fi
    # no FUSE needed (CI containers)
    APPIMAGE_EXTRACT_AND_RUN=1 ARCH="$APPIMAGE_ARCH" "$TOOL" --no-appstream "$APPDIR" "dist/AssetStudio-${APPIMAGE_ARCH}.AppImage"
    echo "Done: dist/AssetStudio-${APPIMAGE_ARCH}.AppImage"
fi

if [[ " $FORMATS " == *" deb "* ]]; then
    PKG="build/deb-${RID}"
    rm -rf "$PKG"
    mkdir -p "$PKG/DEBIAN" "$PKG/opt/assetstudio" "$PKG/usr/bin" "$PKG/usr/share/applications" "$PKG/usr/share/icons/hicolor/256x256/apps"
    cp -a "$RELEASE/." "$PKG/opt/assetstudio/"
    rm -f "$PKG/opt/assetstudio/install-desktop-entry.sh" "$PKG/opt/assetstudio/assetstudio.desktop"
    ln -s /opt/assetstudio/AssetStudio.Avalonia "$PKG/usr/bin/assetstudio"
    ln -s /opt/assetstudio/AssetStudio.CLI "$PKG/usr/bin/assetstudio-cli"
    desktop_entry /opt/assetstudio/AssetStudio.Avalonia assetstudio > "$PKG/usr/share/applications/assetstudio.desktop"
    cp linux/assetstudio.png "$PKG/usr/share/icons/hicolor/256x256/apps/assetstudio.png"
    cat > "$PKG/DEBIAN/control" <<CONTROL
Package: assetstudio
Version: ${VERSION}
Architecture: ${DEB_ARCH}
Maintainer: AssetStudio Linux port
Section: graphics
Priority: optional
Depends: libc6 (>= 2.28), libfontconfig1, libice6, libsm6, libx11-6
Recommends: libvulkan1, libpulse0
Description: Unity asset extraction tool (AssetStudio)
 Cross-platform GUI (Avalonia) and command line tool to explore, extract
 and export Unity assets and asset bundles.
CONTROL
    chmod -R u+rwX,go+rX,go-w "$PKG"
    OUT="dist/assetstudio_${VERSION}_${DEB_ARCH}.deb"
    dpkg-deb --root-owner-group --build "$PKG" "$OUT"
    echo "Done: $OUT"
fi
