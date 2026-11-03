#!/usr/bin/env bash
# Builds Roamwise-<rid>.AppImage on a Linux machine of the same architecture.
# Usage: tools/build-appimage.sh linux-x64|linux-arm64 [extra dotnet publish args, e.g. -p:Version=1.2.0]
set -euo pipefail
rid="$1"; shift
case "$rid" in
  linux-x64) arch=x86_64 ;;
  linux-arm64) arch=aarch64 ;;
  *) echo "unknown rid $rid"; exit 1 ;;
esac
root="$(cd "$(dirname "$0")/.." && pwd)"
out="$root/out/$rid"
app="$root/out/AppDir-$rid"

dotnet publish "$root/src/Roamwise.Linux/Roamwise.Linux.csproj" -c Release -r "$rid" -o "$out" "$@"

rm -rf "$app"
mkdir -p "$app/usr/bin"
cp "$out/roamwise" "$app/usr/bin/roamwise"
cp "$root/src/Roamwise.Linux/Packaging/AppRun" "$app/AppRun"
cp "$root/src/Roamwise.Linux/Packaging/roamwise.desktop" "$app/roamwise.desktop"
cp "$root/src/Roamwise.Linux/Assets/roamwise.png" "$app/roamwise.png"
chmod +x "$app/AppRun" "$app/usr/bin/roamwise"

tool="$root/out/appimagetool-$arch"
[ -x "$tool" ] || { curl -fsSL -o "$tool" "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-$arch.AppImage"; chmod +x "$tool"; }
# Extract-and-run so it works without FUSE (CI runners, containers).
APPIMAGE_EXTRACT_AND_RUN=1 ARCH="$arch" "$tool" --no-appstream "$app" "$root/Roamwise-$rid.AppImage"
echo "Built $root/Roamwise-$rid.AppImage"
