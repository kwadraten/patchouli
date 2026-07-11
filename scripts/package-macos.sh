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

if [[ ! -f "$macos_dir/appsettings.json" ]]; then
  echo "Published appsettings.json was not found in Contents/MacOS." >&2
  exit 1
fi
mv "$macos_dir/appsettings.json" "$resources_dir/appsettings.json"

sed "s/__VERSION__/$version/g" "$root/packaging/macos/Info.plist.template" > "$contents_dir/Info.plist"
plutil -lint "$contents_dir/Info.plist"

iconset="$(mktemp -d)/AppIcon.iconset"
mkdir -p "$iconset"
for size in 16 32 128 256 512; do
  sips -z "$size" "$size" "$root/logo/icon.png" --out "$iconset/icon_${size}x${size}.png" >/dev/null
  double=$((size * 2))
  sips -z "$double" "$double" "$root/logo/icon.png" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$resources_dir/AppIcon.icns"

settings_count="$(find "$contents_dir" -type f -name appsettings.json | wc -l | tr -d '[:space:]')"
if [[ "$settings_count" != "1" || ! -f "$resources_dir/appsettings.json" || -e "$macos_dir/appsettings.json" ]]; then
  echo "appsettings.json must exist exactly once, at Contents/Resources/appsettings.json." >&2
  exit 1
fi

if [[ -n "${APPLE_CODESIGN_IDENTITY:-}" ]]; then
  sign_identity="$APPLE_CODESIGN_IDENTITY"
  sign_options=(--options runtime --timestamp)
else
  sign_identity="-"
  sign_options=()
fi

# Sign every native payload first, then seal the outer application bundle.
while IFS= read -r -d '' native_file; do
  if file "$native_file" | grep -q 'Mach-O'; then
    codesign --force "${sign_options[@]}" --sign "$sign_identity" "$native_file"
  fi
done < <(find "$macos_dir" -type f -print0)
codesign --force "${sign_options[@]}" --sign "$sign_identity" "$app_dir"
codesign --verify --deep --strict --verbose=2 "$app_dir"

rm -f "$dmg_path"
staging="$(mktemp -d)"
trap 'rm -rf "$staging"' EXIT
cp -R "$app_dir" "$staging/Patchouli.Net.app"
ln -s /Applications "$staging/Applications"
hdiutil create -volname "Patchouli.Net" -srcfolder "$staging" -ov -format UDZO "$dmg_path"
echo "$dmg_path"
