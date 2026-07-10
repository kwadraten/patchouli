#!/usr/bin/env bash
set -euo pipefail

runtime="${1:-osx-arm64}"
configuration="${CONFIGURATION:-Release}"
version="${VERSION:-0.1.0}"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
publish_dir="$root/artifacts/publish/$runtime"
app_dir="$root/artifacts/macos/Patchouli.Net.app"
contents_dir="$app_dir/Contents"
macos_dir="$contents_dir/MacOS"
resources_dir="$contents_dir/Resources"
dmg_dir="$root/artifacts/installer"
dmg_path="$dmg_dir/Patchouli.Net-$version-$runtime.dmg"

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "DMG creation requires macOS because hdiutil and codesign are Apple platform tools." >&2
  exit 2
fi

case "$runtime" in
  osx-arm64) rust_target="aarch64-apple-darwin" ;;
  osx-x64) rust_target="x86_64-apple-darwin" ;;
  *) echo "Unsupported macOS runtime: $runtime" >&2; exit 2 ;;
esac

rustup target add "$rust_target"
cargo build --release --locked --target "$rust_target" --manifest-path "$root/tools/patchouli-hayagriva/Cargo.toml"

rm -rf "$publish_dir" "$app_dir"
mkdir -p "$publish_dir" "$macos_dir" "$resources_dir" "$dmg_dir"
dotnet publish "$root/src/Patchouli.UI/Patchouli.UI.csproj" \
  -c "$configuration" \
  -r "$runtime" \
  --self-contained true \
  -p:Version="$version" \
  -p:PublishSingleFile=false \
  -p:DebugType=None \
  -p:DebugSymbols=false \
  -o "$publish_dir"

cp -R "$publish_dir/." "$macos_dir/"
cp "$root/tools/patchouli-hayagriva/target/$rust_target/release/patchouli-hayagriva" "$macos_dir/patchouli-hayagriva"
chmod +x "$macos_dir/Patchouli.UI" "$macos_dir/patchouli-hayagriva"
sed "s/__VERSION__/$version/g" "$root/packaging/macos/Info.plist.template" > "$contents_dir/Info.plist"

iconset="$(mktemp -d)/AppIcon.iconset"
mkdir -p "$iconset"
for size in 16 32 128 256 512; do
  sips -z "$size" "$size" "$root/logo/icon.png" --out "$iconset/icon_${size}x${size}.png" >/dev/null
  double=$((size * 2))
  sips -z "$double" "$double" "$root/logo/icon.png" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$resources_dir/AppIcon.icns"

if [[ -n "${APPLE_CODESIGN_IDENTITY:-}" ]]; then
  codesign --force --deep --options runtime --sign "$APPLE_CODESIGN_IDENTITY" "$app_dir"
else
  codesign --force --deep --sign - "$app_dir"
fi

rm -f "$dmg_path"
staging="$(mktemp -d)"
trap 'rm -rf "$staging"' EXIT
cp -R "$app_dir" "$staging/Patchouli.Net.app"
ln -s /Applications "$staging/Applications"
hdiutil create -volname "Patchouli.Net" -srcfolder "$staging" -ov -format UDZO "$dmg_path"
echo "$dmg_path"
