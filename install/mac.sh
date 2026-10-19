#!/bin/bash
# Installs or updates DropDock on this Mac. Safe to run again at any time.
#
#   curl -fsSL https://raw.githubusercontent.com/myfxpaddy/dropdock/main/install/mac.sh | bash
#
# Downloads the latest release, puts DropDock.app in Applications, starts it (it then starts at every
# login, adds itself to the Dock and puts an alias on the Desktop) and checks that it is running.
set -euo pipefail

REPO="myfxpaddy/dropdock"
LABEL="app.dropdock.agent"
DOMAIN="gui/$(id -u)"

say() { printf '==> %s\n' "$1"; }
fail() { printf 'Error: %s\n' "$1" >&2; exit 1; }

[ "$(uname)" = "Darwin" ] || fail "This script is for macOS. On Windows use install/windows.ps1."
VERSION="$(sw_vers -productVersion)"
[ "${VERSION%%.*}" -ge 13 ] || fail "DropDock needs macOS 13 or newer. This Mac has $VERSION."

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

say "Downloading DropDock"
if ! curl -fsSL -o "$TMP/DropDock-mac.zip" "https://github.com/$REPO/releases/latest/download/DropDock-mac.zip"; then
  if command -v gh >/dev/null 2>&1 && gh release download --repo "$REPO" --pattern DropDock-mac.zip --dir "$TMP" --clobber; then
    :
  else
    fail "Could not download DropDock. If the repository is private, run 'gh auth login' first, then run this again."
  fi
fi
ditto -x -k "$TMP/DropDock-mac.zip" "$TMP"
[ -d "$TMP/DropDock.app" ] || fail "The download did not contain DropDock.app."

if [ -w /Applications ]; then DEST="/Applications"; else DEST="$HOME/Applications"; mkdir -p "$DEST"; fi
APP="$DEST/DropDock.app"

if launchctl print "$DOMAIN/$LABEL" >/dev/null 2>&1; then
  say "Stopping the running copy"
  launchctl bootout "$DOMAIN/$LABEL" 2>/dev/null || true
fi
pkill -f "DropDock.app/Contents/MacOS/DropDock" 2>/dev/null || true
sleep 1

say "Installing to $APP"
rm -rf "$APP"
ditto "$TMP/DropDock.app" "$APP"
# curl does not mark downloads as quarantined, but clear it anyway in case the zip came another way.
xattr -dr com.apple.quarantine "$APP" 2>/dev/null || true

say "Starting DropDock"
open "$APP"

for _ in $(seq 1 20); do
  pgrep -f "DropDock.app/Contents/MacOS/DropDock" >/dev/null && break
  sleep 1
done
sleep 3
pgrep -f "DropDock.app/Contents/MacOS/DropDock" >/dev/null ||
  fail "DropDock did not start. Open $APP from Finder; if macOS blocks it, go to System Settings > Privacy & Security and click Open Anyway."

cat <<EOF

DropDock is installed and running ($APP).

  1. Click Allow when macOS asks about finding devices on the local network, and about the
     Documents and Desktop folders. Those questions appear on screen.
  2. Open the bar: rest the pointer at the top edge of a screen, or click DropDock in the Dock.
  3. Click one of your other computers marked "Click to link", check both show the same
     six-digit code, and click Allow on the other computer.

Log: ~/Library/Application Support/DropDock/dropdock.log
EOF
