#!/usr/bin/env bash
# Builds Linux packages for Patchouli.Net: deb, rpm, AppImage, Flatpak.
#
# Usage: scripts/package-linux.sh [deb] [rpm] [appimage] [flatpak]
#   With no arguments, all four formats are built.
#
# Environment:
#   VERSION       Application version (default: 0.3.8, must match Changelog.md)
#   RUNTIME       .NET runtime identifier (default: linux-x64)
#   SKIP_PUBLISH  Set to 1 to reuse the existing .tmp/publish output instead of
#                 running dotnet publish / cargo build again.
#
# Prerequisites (built by default unless SKIP_PUBLISH=1):
#   .tmp/publish/linux-x64            self-contained Patchouli.UI publish
#   .tmp/publish/linux-x64-cli        self-contained patchouli-cli publish
#   tools/biblatex-helper/target/release/biblatex-helper
#
# Outputs land in .tmp/installer/.
set -euo pipefail

# /tmp is often a tiny tmpfs in containers; keep all temp work on disk.
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export TMPDIR="$root/.tmp"

version="${VERSION:-0.3.8}"
runtime="${RUNTIME:-linux-x64}"
skip_publish="${SKIP_PUBLISH:-0}"
publish_ui="$root/.tmp/publish/$runtime"
publish_cli="$root/.tmp/publish/$runtime-cli"
helper_src="$root/tools/biblatex-helper/target/release/biblatex-helper"
stage="$root/.tmp/linux-stage"
out="$root/.tmp/installer"
app_id="io.github.kwadraten.patchouli"
maintainer="kwadraten <schild0van0man@duck.com>"

formats=("$@")
if [[ ${#formats[@]} -eq 0 ]]; then
  formats=(deb rpm appimage flatpak)
fi

want() {
  local f="$1"; shift
  for x in "$@"; do [[ "$x" == "$f" ]] && return 0; done
  return 1
}

publish() {
  if [[ "$skip_publish" == "1" ]]; then
    echo "SKIP_PUBLISH=1: reusing existing publish output."
  else
    echo "Publishing Patchouli.UI ($runtime)..."
    dotnet publish "$root/src/Patchouli.UI/Patchouli.UI.csproj" \
      -c Release -r "$runtime" --self-contained true \
      -p:Version="$version" -p:DebugType=None -p:DebugSymbols=false \
      -o "$publish_ui"
    echo "Publishing patchouli-cli ($runtime)..."
    dotnet publish "$root/src/Patchouli.Cli/Patchouli.Cli.csproj" \
      -c Release -r "$runtime" --self-contained true \
      -p:Version="$version" -p:DebugType=None -p:DebugSymbols=false \
      -o "$publish_cli"
    echo "Building biblatex-helper..."
    cargo build --release --manifest-path "$root/tools/biblatex-helper/Cargo.toml"
  fi

  [[ -x "$publish_ui/Patchouli.UI" ]] || { echo "Missing $publish_ui/Patchouli.UI" >&2; exit 1; }
  [[ -x "$publish_cli/patchouli-cli" ]] || { echo "Missing $publish_cli/patchouli-cli" >&2; exit 1; }
  [[ -x "$helper_src" ]] || { echo "Missing $helper_src" >&2; exit 1; }
}

normalize_perms() {
  # dpkg/rpm/appimagetool want 755 dirs and 644 files,
  # keeping the executable bit where it was set.
  # Note: -perm /111 (any exec bit), NOT -perm -111 (all exec bits).
  local dir="$1"
  find "$dir" -type d -exec chmod 755 {} +
  find "$dir" -type f -not -perm /111 -exec chmod 644 {} +
  find "$dir" -type f -perm /111 -exec chmod 755 {} +
}

stage_tree() {
  echo "Staging install tree..."
  rm -rf "$stage"
  mkdir -p "$stage/usr/lib/patchouli" "$stage/usr/lib/patchouli-cli" \
           "$stage/usr/bin" "$stage/usr/share/applications" \
           "$stage/usr/share/metainfo"

  cp -R "$publish_ui/." "$stage/usr/lib/patchouli/"
  cp -R "$publish_cli/." "$stage/usr/lib/patchouli-cli/"
  cp "$helper_src" "$stage/usr/lib/patchouli/biblatex-helper"
  cp "$helper_src" "$stage/usr/lib/patchouli-cli/biblatex-helper"
  chmod +x "$stage/usr/lib/patchouli/Patchouli.UI" \
           "$stage/usr/lib/patchouli-cli/patchouli-cli" \
           "$stage/usr/lib/patchouli/biblatex-helper" \
           "$stage/usr/lib/patchouli-cli/biblatex-helper"

  ln -s ../lib/patchouli/Patchouli.UI "$stage/usr/bin/patchouli"
  ln -s ../lib/patchouli-cli/patchouli-cli "$stage/usr/bin/patchouli-cli"

  # Icons (hicolor)
  for size in 16 32 48 64 128 256 512; do
    icondir="$stage/usr/share/icons/hicolor/${size}x${size}/apps"
    mkdir -p "$icondir"
    convert "$root/logo/icon.png" -resize "${size}x${size}" "$icondir/$app_id.png"
  done

  # Desktop entry + AppStream metadata
  sed "s|__EXEC__|/usr/bin/patchouli|" \
    "$root/packaging/linux/assets/$app_id.desktop" \
    > "$stage/usr/share/applications/$app_id.desktop"
  cp "$root/packaging/linux/assets/$app_id.metainfo.xml" \
    "$stage/usr/share/metainfo/"

  cp "$root/LICENSE" "$stage/usr/lib/patchouli/LICENSE"
  mkdir -p "$out"
  normalize_perms "$stage"
}

build_deb() {
  echo "Building deb..."
  deb_stage="$root/.tmp/linux-stage-deb"
  rm -rf "$deb_stage"
  cp -a --no-preserve=ownership "$stage" "$deb_stage"
  mkdir -p "$deb_stage/DEBIAN"
  chmod 755 "$deb_stage/DEBIAN"
  installed_size="$(du -sk "$stage" | cut -f1)"
  cat > "$deb_stage/DEBIAN/control" <<EOF
Package: patchouli
Version: $version
Section: office
Priority: optional
Architecture: amd64
Maintainer: $maintainer
Depends: fontconfig
Description: Personal literature manager for large PDF libraries
 Patchouli is an experimental desktop literature manager for managing,
 digitizing, continuously correcting, and effectively recalling large PDF
 libraries, with reproducible evidence citations and AI agent collaboration.
EOF
  chmod 644 "$deb_stage/DEBIAN/control"
  # Assemble the deb manually with ar: dpkg-deb's internal compressor pipeline
  # is fragile when /tmp is a small tmpfs; tar+zstd+ar are verified to work.
  deb_work="$root/.tmp/deb-work"
  rm -rf "$deb_work"
  mkdir -p "$deb_work"
  tar -cf "$deb_work/data.tar" -C "$deb_stage" --exclude=DEBIAN .
  tar -cf "$deb_work/control.tar" -C "$deb_stage/DEBIAN" .
  zstd -q "$deb_work/data.tar" -o "$deb_work/data.tar.zst"
  zstd -q "$deb_work/control.tar" -o "$deb_work/control.tar.zst"
  echo "2.0" > "$deb_work/debian-binary"
  rm -f "$out/patchouli-${version}-linux-x64.deb"
  (cd "$deb_work" && ar r "$out/patchouli-${version}-linux-x64.deb" \
    debian-binary control.tar.zst data.tar.zst)
  rm -rf "$deb_work"
  echo "deb: $out/patchouli-${version}-linux-x64.deb"
}

build_rpm() {
  echo "Building rpm..."
  rpm_topdir="$root/.tmp/rpmbuild"
  rm -rf "$rpm_topdir"
  mkdir -p "$rpm_topdir"/{BUILD,RPMS,SOURCES,SPECS,SRPMS}
  cp "$root/packaging/linux/rpm/patchouli.spec" "$rpm_topdir/SPECS/"
  # Source0 is a tarball of the staged usr/ tree plus LICENSE at its root.
  tmp_src="$(mktemp -d)"
  cp -R "$stage/usr" "$tmp_src/"
  cp "$root/LICENSE" "$tmp_src/"
  tar -czf "$rpm_topdir/SOURCES/patchouli-${version}.tar.gz" -C "$tmp_src" usr LICENSE
  rm -rf "$tmp_src"
  rpmbuild -bb \
    --define "_topdir $rpm_topdir" \
    --define "app_version $version" \
    "$rpm_topdir/SPECS/patchouli.spec"
  find "$rpm_topdir/RPMS" -name "*.rpm" -exec cp {} "$out/" \;
  echo "rpm: $(ls "$out"/*.rpm)"
}

build_appimage() {
  echo "Building AppImage..."
  appdir="$root/.tmp/Patchouli.AppDir"
  rm -rf "$appdir"
  mkdir -p "$appdir/usr/bin" "$appdir/usr/lib"
  cp -R "$publish_ui/." "$appdir/usr/bin/"
  cp "$helper_src" "$appdir/usr/bin/biblatex-helper"
  chmod +x "$appdir/usr/bin/Patchouli.UI" "$appdir/usr/bin/biblatex-helper"
  sed "s|__EXEC__|Patchouli.UI|" \
    "$root/packaging/linux/assets/$app_id.desktop" \
    > "$appdir/$app_id.desktop"
  convert "$root/logo/icon.png" -resize "256x256" "$appdir/$app_id.png"
  ln -s "$app_id.png" "$appdir/.DirIcon"
  cat > "$appdir/AppRun" <<'EOF'
#!/usr/bin/env bash
HERE="$(dirname "$(readlink -f "$0")")"
export PATH="$HERE/usr/bin:$PATH"
exec "$HERE/usr/bin/Patchouli.UI" "$@"
EOF
  chmod +x "$appdir/AppRun"
  normalize_perms "$appdir"

  appimagetool="$root/.tmp/appimagetool"
  if [[ ! -x "$appimagetool" ]]; then
    echo "Downloading appimagetool..."
    curl -sSL -o "$appimagetool" \
      "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage"
    chmod +x "$appimagetool"
  fi
  # appimagetool is itself an AppImage and needs FUSE; extract it once and
  # run the extracted AppRun instead (works without FUSE).
  appimagetool_extracted="$root/.tmp/appimagetool.extracted"
  if [[ ! -x "$appimagetool_extracted/AppRun" ]]; then
    echo "Extracting appimagetool (no FUSE)..."
    rm -rf "$appimagetool_extracted"
    (cd "$root/.tmp" && "$appimagetool" --appimage-extract >/dev/null 2>&1)
    mv "$root/.tmp/squashfs-root" "$appimagetool_extracted"
  fi
  (cd "$root/.tmp" && ARCH=x86_64 "$appimagetool_extracted/AppRun" "$appdir" \
    "$out/Patchouli.Net-${version}-linux-x64.AppImage")
  echo "AppImage: $out/Patchouli.Net-${version}-linux-x64.AppImage"
}

build_flatpak() {
  echo "Building Flatpak..."
  # flatpak-builder's build sandbox needs BPF/seccomp which containers often
  # block ("Failed to export bpf"). Assemble manually instead:
  # build-init, copy files, build-finish, build-export, build-bundle.
  export TMPDIR="$root/.tmp"
  build_dir="$root/.tmp/flatpak-manual/build"
  repo_dir="$root/.tmp/flatpak-repo"
  rm -rf "$root/.tmp/flatpak-manual" "$repo_dir"
  mkdir -p "$repo_dir"
  flatpak build-init "$build_dir" "$app_id" \
    org.freedesktop.Sdk org.freedesktop.Platform 24.08
  cp -a --no-preserve=ownership "$stage/usr/." "$build_dir/files/"
  # /usr/bin/patchouli{,-cli} symlinks already come from the staged tree.
  # build-finish writes metadata; its export phase may hit container chown
  # limits but metadata is written before that, and build-export is what matters.
  flatpak build-finish "$build_dir" \
    --share=ipc --socket=x11 --socket=wayland --socket=pulseaudio \
    --device=dri --filesystem=home \
    --talk-name=org.freedesktop.Notifications \
    --command=patchouli 2>&1 | grep -v "fchown" || true
  flatpak build-export "$repo_dir" "$build_dir"
  flatpak build-bundle "$repo_dir" \
    "$out/${app_id}-${version}-x86_64.flatpak" "$app_id"
  echo "Flatpak: $out/${app_id}-${version}-x86_64.flatpak"
}

publish
stage_tree
want deb "${formats[@]}" && build_deb
want rpm "${formats[@]}" && build_rpm
want appimage "${formats[@]}" && build_appimage
want flatpak "${formats[@]}" && build_flatpak

echo "Done. Artifacts in $out:"
ls -la "$out"
