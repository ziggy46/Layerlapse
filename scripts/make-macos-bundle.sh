#!/usr/bin/env bash
# Builds a local, ad-hoc signed Layerlapse.app for development on macOS (not for distribution).
# A bundle gives the app a stable identity for Keychain access and the Local Network permission prompt.
# Usage: scripts/make-macos-bundle.sh [Debug|Release]
set -euo pipefail

config="${1:-Debug}"
root="$(cd "$(dirname "$0")/.." && pwd)"
case "$(uname -m)" in
  arm64) rid=osx-arm64 ;;
  *) rid=osx-x64 ;;
esac

app="$root/artifacts/macos/Layerlapse.app"
rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"

dotnet publish "$root/src/Layerlapse.App" -c "$config" -r "$rid" --self-contained true \
  -p:UseAppHost=true -o "$app/Contents/MacOS" >/dev/null

version="$(git -C "$root" describe --always --dirty 2>/dev/null || echo dev)"
cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Layerlapse</string>
  <key>CFBundleDisplayName</key><string>Layerlapse</string>
  <key>CFBundleIdentifier</key><string>app.layerlapse.dev</string>
  <key>CFBundleExecutable</key><string>Layerlapse</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>0.0.0</string>
  <key>CFBundleVersion</key><string>${version}</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSLocalNetworkUsageDescription</key><string>Layerlapse looks for your 3D printer on the local network and downloads its timelapses.</string>
</dict>
</plist>
PLIST

codesign --force --deep --sign - "$app" >/dev/null 2>&1
echo "$app"
