Name:           patchouli
Version:        %{app_version}
Release:        1%{?dist}
Summary:        Personal literature manager for large PDF libraries
License:        GPL-3.0-or-later
URL:            https://github.com/kwadraten/patchouli
Source0:        %{name}-%{version}.tar.gz

BuildArch:      x86_64
Requires:       fontconfig

%description
Patchouli is an experimental desktop literature manager for managing,
digitizing, continuously correcting, and effectively recalling large PDF
libraries, with reproducible evidence citations and AI agent collaboration.
It tracks PDF files by content hash, keeps OCR corrections in an immutable
page-level bounding-box tree, and ships an MCP server so AI agents can
explore and maintain your library.

%install
# The staging tree is prepared by scripts/package-linux.sh and passed as Source0.
# --no-same-owner: containers often block chown even for root.
tar -xzf %{SOURCE0} -C "$RPM_BUILD_ROOT" --no-same-owner
mkdir -p "$RPM_BUILD_ROOT/usr/share/licenses/%{name}-%{version}"
cp "$RPM_BUILD_ROOT/LICENSE" "$RPM_BUILD_ROOT/usr/share/licenses/%{name}-%{version}/"
rm "$RPM_BUILD_ROOT/LICENSE"

%files
/usr/lib/patchouli/
/usr/lib/patchouli-cli/
/usr/bin/patchouli
/usr/bin/patchouli-cli
/usr/share/applications/io.github.kwadraten.patchouli.desktop
/usr/share/icons/hicolor/*/apps/io.github.kwadraten.patchouli.png
/usr/share/metainfo/io.github.kwadraten.patchouli.metainfo.xml
%license LICENSE

%changelog
* Fri Oct 09 2026 kwadraten <schild0van0man@duck.com> - 0.3.8-1
- Linux packaging: rpm, deb, AppImage, Flatpak
