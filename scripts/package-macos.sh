#!/usr/bin/env bash
set -euo pipefail

# Patchouli on macOS is intentionally not sandboxed and is not targeting the Mac App Store.
# File access relies on the standard TCC folder picker prompts (NS*FolderUsageDescription in Info.plist).
# The completed bundle is ad-hoc signed for integrity, without sandbox provisioning.
# This does not provide Developer ID trust or notarization; see ADR 0017.

runtime="${1:-osx-arm64}"
configuration="${CONFIGURATION:-Release}"
version="${VERSION:-0.3.14}"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
publish_dir="$root/.tmp/publish/$runtime"
app_dir="$root/.tmp/macos/Patchouli.NET.app"
contents_dir="$app_dir/Contents"
macos_dir="$contents_dir/MacOS"
resources_dir="$contents_dir/Resources"
dmg_dir="$root/.tmp/installer"
dmg_path="$dmg_dir/Patchouli.NET-$version-$runtime.dmg"

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "DMG creation requires macOS because hdiutil and bundle tools are Apple platform tools." >&2
  exit 2
fi

case "$runtime" in
  osx-arm64) pdfium_arch="arm64"; fs_helper_arch="arm64" ;;
  osx-x64) pdfium_arch="x86_64"; fs_helper_arch="x86_64" ;;
  *) echo "Unsupported macOS runtime: $runtime" >&2; exit 2 ;;
esac

fs_helper_dir="$root/sidecar/patchouli-macos-fs"
fs_helper_dylib="libpatchouli-macos-fs.dylib"
clang -dynamiclib -framework Foundation \
  -arch "$fs_helper_arch" \
  -install_name "@rpath/$fs_helper_dylib" \
  -o "$fs_helper_dir/$fs_helper_dylib" \
  "$fs_helper_dir/patchouli_macos_fs.m"

rm -rf "$publish_dir" "$app_dir"
mkdir -p "$publish_dir" "$macos_dir" "$resources_dir" "$dmg_dir"
dotnet publish "$root/src/Patchouli.UI/Patchouli.UI.csproj" \
  -m:1 \
  -c "$configuration" \
  -r "$runtime" \
  --self-contained true \
  -p:Version="$version" \
  -p:PublishSingleFile=false \
  -p:DebugType=None \
  -p:DebugSymbols=false \
  -p:BuildInParallel=false \
  -p:UseSharedCompilation=false \
  -o "$publish_dir"

cp -R "$publish_dir/." "$macos_dir/"
cp "$fs_helper_dir/$fs_helper_dylib" "$macos_dir/$fs_helper_dylib"
chmod +x "$macos_dir/Patchouli.UI"

helper_bin="$root/sidecar/biblatex-helper/target/release/biblatex-helper"
if [[ ! -x "$helper_bin" ]]; then
  cargo build --release --manifest-path "$root/sidecar/biblatex-helper/Cargo.toml"
fi
if [[ ! -x "$helper_bin" ]]; then
  echo "biblatex-helper was not found at $helper_bin" >&2
  exit 1
fi
cp "$helper_bin" "$macos_dir/biblatex-helper"
chmod +x "$macos_dir/biblatex-helper"

cli_dir="$root/.tmp/cli-publish"
rm -rf "$cli_dir"
dotnet publish "$root/src/Patchouli.Cli/Patchouli.Cli.csproj" \
  -m:1 \
  -c "$configuration" \
  -r "$runtime" \
  --self-contained true \
  -p:Version="$version" \
  -p:DebugType=None \
  -p:DebugSymbols=false \
  -p:BuildInParallel=false \
  -p:UseSharedCompilation=false \
  -o "$cli_dir"
cli_bin="$cli_dir/patchouli-cli"
if [[ ! -x "$cli_bin" ]]; then
  echo "patchouli-cli was not found at $cli_bin" >&2
  exit 1
fi
cp "$cli_bin" "$macos_dir/patchouli-cli"
chmod +x "$macos_dir/patchouli-cli"

if [[ ! -f "$macos_dir/appsettings.json" ]]; then
  echo "Published appsettings.json was not found in Contents/MacOS." >&2
  exit 1
fi
mv "$macos_dir/appsettings.json" "$resources_dir/appsettings.json"

sed "s/__VERSION__/$version/g" "$root/packaging/macos/Info.plist.template" > "$contents_dir/Info.plist"
plutil -lint "$contents_dir/Info.plist"

mkdir -p "$root/.tmp/packaging"
iconset_root="$(mktemp -d "$root/.tmp/packaging/icons.XXXXXX")"
iconset="$iconset_root/AppIcon.iconset"
mkdir -p "$iconset"
for size in 16 32 128 256 512; do
  sips -z "$size" "$size" "$root/logo/icon.png" --out "$iconset/icon_${size}x${size}.png" >/dev/null
  double=$((size * 2))
  sips -z "$double" "$double" "$root/logo/icon.png" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$resources_dir/AppIcon.icns"
rm -rf "$iconset_root"

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

# MacOS is a nested-code location: managed DLLs and data must be resources.
# Relative symlinks preserve .NET's expected layout next to the apphost.
for payload in "$macos_dir"/*; do
  if [[ -f "$payload" && "$(file -b "$payload")" == *"Mach-O"* ]]; then
    continue
  fi
  payload_name="$(basename "$payload")"
  mv "$payload" "$resources_dir/$payload_name"
  ln -s "../Resources/$payload_name" "$payload"
done

# Sign native code explicitly from the inside out; --deep is for verification only.
# Preserve any runtime entitlements carried by the published native dependencies.
# Signing the main executable can implicitly sign the bundle, so leave it until last.
while IFS= read -r -d '' binary; do
  if [[ "$binary" != "$macos_dir/Patchouli.UI" && "$(file -b "$binary")" == *"Mach-O"* ]]; then
    codesign --force --sign - --preserve-metadata=entitlements "$binary"
  fi
done < <(find "$macos_dir" -type f -print0)
codesign --force --sign - "$app_dir"
codesign --verify --deep --strict --verbose=4 "$app_dir"

rm -f "$dmg_path"
staging="$(mktemp -d "$root/.tmp/packaging/dmg.XXXXXX")"
trap 'rm -rf "$staging"' EXIT
cp -R "$app_dir" "$staging/Patchouli.NET.app"
ln -s /Applications "$staging/Applications"
hdiutil create -volname "Patchouli.NET" -srcfolder "$staging" -ov -format UDZO "$dmg_path"
codesign --force --sign - "$dmg_path"
echo "$dmg_path"
