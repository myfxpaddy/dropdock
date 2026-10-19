#!/bin/bash
# Builds DropDock.app (universal: Apple Silicon and Intel) and a zip to copy to other Macs.
set -euo pipefail
cd "$(dirname "$0")"
ROOT="$(cd .. && pwd)"
DIST="$ROOT/dist"
APP="$DIST/DropDock.app"
ICONS="$ROOT/build/icon"

if [ ! -f "$ICONS/AppIcon.icns" ]; then
  swift "$ROOT/tools/make-icon.swift" "$ICONS"
  iconutil -c icns "$ICONS/AppIcon.iconset" -o "$ICONS/AppIcon.icns"
fi

# One build per CPU, then merge, so the same app runs on Apple Silicon and Intel Macs.
for T in arm64-apple-macosx13.0 x86_64-apple-macosx13.0; do
  swift build -c release --triple "$T"
done
BIN="$ROOT/build/DropDock-universal"
mkdir -p "$ROOT/build"
lipo -create -output "$BIN" \
  "$(swift build -c release --triple arm64-apple-macosx13.0 --show-bin-path)/DropDock" \
  "$(swift build -c release --triple x86_64-apple-macosx13.0 --show-bin-path)/DropDock"

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/DropDock"
cp Resources/Info.plist "$APP/Contents/Info.plist"
cp "$ICONS/AppIcon.icns" "$APP/Contents/Resources/AppIcon.icns"
cp "$ROOT"/install/prompt-*.txt "$APP/Contents/Resources/"
strip -x "$APP/Contents/MacOS/DropDock" 2>/dev/null || true
codesign --force --sign - --timestamp=none "$APP"

rm -f "$DIST/DropDock-mac.zip"
(cd "$DIST" && ditto -c -k --keepParent DropDock.app DropDock-mac.zip)
echo "Built $APP"
du -sh "$APP" "$DIST/DropDock-mac.zip"
