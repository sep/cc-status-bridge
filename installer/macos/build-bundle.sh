#!/usr/bin/env bash
# -----------------------------------------------------------------------------
# build-bundle.sh — assemble ClaudePanelBridge.app and wrap in a .dmg
#
# Usage:
#   build-bundle.sh <version> <rid> <binary-path>
#
# e.g.
#   build-bundle.sh 0.2.2 osx-arm64 out/osx-arm64/ClaudeStatusBridge-osx-arm64
#
# Produces (in $PWD):
#   ClaudePanelBridge-<version>-<rid>.dmg
# -----------------------------------------------------------------------------
set -euo pipefail

VERSION="${1:?usage: build-bundle.sh <version> <rid> <binary-path>}"
RID="${2:?missing rid (osx-arm64 or osx-x64)}"
BINARY="${3:?missing binary path}"

[ -f "$BINARY" ] || { echo "no such binary: $BINARY" >&2; exit 1; }

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
WORKDIR="$(mktemp -d)"
APP_DIR="$WORKDIR/ClaudePanelBridge.app"
APP_CONTENTS="$APP_DIR/Contents"
APP_MACOS="$APP_CONTENTS/MacOS"
APP_RESOURCES="$APP_CONTENTS/Resources"

mkdir -p "$APP_MACOS" "$APP_RESOURCES"

# Info.plist (templated with version)
sed "s/__VERSION__/${VERSION}/g" "$SCRIPT_DIR/Info.plist.template" \
    > "$APP_CONTENTS/Info.plist"

# Binary
cp "$BINARY" "$APP_MACOS/ClaudeStatusBridge"
chmod +x "$APP_MACOS/ClaudeStatusBridge"

# Re-sign the bundle ad-hoc (replaces the per-binary ad-hoc signature with
# one that covers the whole bundle, which is what Gatekeeper expects on
# .app bundles).
codesign --force --deep --sign - "$APP_DIR"
codesign --verify --verbose "$APP_DIR"

# Stage for .dmg: the .app and a symlink to /Applications side by side.
DMG_STAGING="$WORKDIR/dmg-staging"
mkdir -p "$DMG_STAGING"
cp -R "$APP_DIR" "$DMG_STAGING/"
ln -s /Applications "$DMG_STAGING/Applications"

DMG_PATH="$(pwd)/ClaudePanelBridge-${VERSION}-${RID}.dmg"
rm -f "$DMG_PATH"
hdiutil create \
    -volname "ClaudePanel Bridge ${VERSION}" \
    -srcfolder "$DMG_STAGING" \
    -ov \
    -format UDZO \
    "$DMG_PATH"

# Cleanup workdir; the .dmg is what we return.
rm -rf "$WORKDIR"
echo "wrote $DMG_PATH"
