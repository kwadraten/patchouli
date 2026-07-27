#!/usr/bin/env bash
set -euo pipefail

runtime="${1:-osx-arm64}"
configuration="${CONFIGURATION:-Release}"
version="${VERSION:-0.2.1}"
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
  osx-arm64) pdfium_arch="arm64" ;;
  osx-x64) pdfium_arch="x86_64" ;;
  *) echo "Unsupported macOS runtime: $runtime" >&2; exit 2 ;;
esac

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
chmod +x "$macos_dir/Patchouli.UI"

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

pdfium_path="$macos_dir/libpdfium.dylib"
if [[ ! -f "$pdfium_path" ]]; then
  echo "PDFium native library was not published for $runtime." >&2
  exit 1
fi
pdfium_description="$(file "$pdfium_path")"
if [[ "$pdfium_description" != *"Mach-O"* || "$pdfium_description" != *"$pdfium_arch"* ]]; then
  echo "PDFium native library has the wrong format or architecture: $pdfium_description" >&2
  exit 1
fi
if find "$macos_dir" -type f -iname '*mupdf*' -print -quit | grep -q .; then
  echo "A forbidden MuPDF payload remains in the macOS application bundle." >&2
  exit 1
fi

# Exercise the native library on the packaging host before sealing the bundle.
dotnet test "$root/tests/Patchouli.Tests/Patchouli.Tests.csproj" -c "$configuration" \
  --filter 'FullyQualifiedName~RealPdfRendererTests|FullyQualifiedName~MinerUUploadPreparerTests.UploadAndExtract_splits_pdf_when_page_limit_would_be_exceeded'

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
