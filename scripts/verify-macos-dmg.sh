#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dmg_path="${1:?Usage: verify-macos-dmg.sh path/to/release.dmg}"

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "DMG signature verification requires macOS." >&2
  exit 2
fi

hdiutil verify "$dmg_path"
codesign --verify --strict --verbose=4 "$dmg_path"
mkdir -p "$root/.tmp/packaging"
mount_dir="$(mktemp -d "$root/.tmp/packaging/verify-dmg.XXXXXX")"
cleanup() {
  if mountpoint_info="$(hdiutil info)" && [[ "$mountpoint_info" == *"$mount_dir"* ]]; then
    hdiutil detach "$mount_dir"
  fi
  rmdir "$mount_dir"
}
trap cleanup EXIT
hdiutil attach "$dmg_path" -readonly -nobrowse -mountpoint "$mount_dir"

app_dir="$mount_dir/Patchouli.NET.app"
test -s "$app_dir/Contents/_CodeSignature/CodeResources"
plutil -lint "$app_dir/Contents/_CodeSignature/CodeResources"
codesign --verify --deep --strict --verbose=4 "$app_dir"
signature_details="$(codesign --display --verbose=4 "$app_dir" 2>&1)"
printf '%s\n' "$signature_details"
grep -q '^Signature=adhoc$' <<< "$signature_details"
grep -q '^Sealed Resources version=2 ' <<< "$signature_details"
if grep -q '^Info.plist=not bound$' <<< "$signature_details"; then
  echo "The app's Info.plist must be bound to its bundle signature." >&2
  exit 1
fi
echo "Verified ad-hoc app bundle signature, bound Info.plist, and sealed resources in $dmg_path"
