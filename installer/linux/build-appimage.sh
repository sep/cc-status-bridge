#!/usr/bin/env bash
# -----------------------------------------------------------------------------
# build-appimage.sh — wrap the linux-x64 binary into an AppImage
#
# Usage:
#   build-appimage.sh <version> <binary-path>
#
# Produces (in $PWD):
#   ClaudePanelBridge-<version>-x86_64.AppImage
# -----------------------------------------------------------------------------
set -euo pipefail

VERSION="${1:?usage: build-appimage.sh <version> <binary-path>}"
BINARY="${2:?missing binary path}"

[ -f "$BINARY" ] || { echo "no such binary: $BINARY" >&2; exit 1; }

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
WORKDIR="$(mktemp -d)"
APPDIR="$WORKDIR/ClaudePanelBridge.AppDir"

mkdir -p "$APPDIR/usr/bin"
cp "$BINARY" "$APPDIR/usr/bin/ClaudeStatusBridge"
chmod +x "$APPDIR/usr/bin/ClaudeStatusBridge"

cp "$SCRIPT_DIR/AppRun" "$APPDIR/AppRun"
chmod +x "$APPDIR/AppRun"

cp "$SCRIPT_DIR/claudepanel-bridge.desktop" "$APPDIR/claudepanel-bridge.desktop"

# AppImage requires an icon; render a 256x256 plain PNG so we don't have to
# ship asset files. ImageMagick is available on GitHub-hosted Ubuntu runners.
convert -size 256x256 xc:'#159957' "$APPDIR/claudepanel-bridge.png"

# Fetch appimagetool if it's not on PATH already (CI runners typically don't
# have it pre-installed).
if ! command -v appimagetool >/dev/null 2>&1; then
    APPIMAGETOOL="$WORKDIR/appimagetool"
    curl -sSL -o "$APPIMAGETOOL" \
        https://github.com/AppImage/AppImageKit/releases/download/continuous/appimagetool-x86_64.AppImage
    chmod +x "$APPIMAGETOOL"
else
    APPIMAGETOOL="$(command -v appimagetool)"
fi

OUTPUT="$(pwd)/ClaudePanelBridge-${VERSION}-x86_64.AppImage"
rm -f "$OUTPUT"

# AppImageTool runs itself as an AppImage; on systems without FUSE we need
# --appimage-extract-and-run. Always pass it so this works in any CI runner.
ARCH=x86_64 "$APPIMAGETOOL" --appimage-extract-and-run "$APPDIR" "$OUTPUT"

rm -rf "$WORKDIR"
echo "wrote $OUTPUT"
