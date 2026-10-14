Name:           patchouli
Version:        %{app_version}
Release:        1%{?dist}
Summary:        Personal literature manager for large PDF libraries
License:        GPL-3.0-or-later
URL:            https://github.com/kwadraten/patchouli
Source0:        %{name}-%{version}.tar.gz

BuildArch:      x86_64
# Runtime deps mirror the DEB (Avalonia X11 + .NET 10 self-contained).
# libicu/openssl/krb5 names vary by distro; use unversioned where possible.
Requires:       fontconfig
Requires:       libX11
Requires:       libICE
Requires:       libSM
Requires:       libicu
Requires:       openssl-libs
Requires:       krb5-libs

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
# Exclude .NET LTTng trace provider: optional diagnostic component requiring
# liblttng-ust.so.0 which is not available on Fedora 44 (provides only so.1).
# The normal tested runtime (libcoreclr.so) does not need it; it is only
# loaded when LTTng-based diagnostics are explicitly enabled.
rm -f "$RPM_BUILD_ROOT"/usr/lib/patchouli/libcoreclrtraceptprovider.so \
      "$RPM_BUILD_ROOT"/usr/lib/patchouli-cli/libcoreclrtraceptprovider.so

%files
/usr/lib/patchouli/
/usr/lib/patchouli-cli/
/usr/bin/patchouli
/usr/bin/patchouli-cli
/usr/share/applications/io.github.kwadraten.patchouli.desktop
/usr/share/icons/hicolor/*/apps/io.github.kwadraten.patchouli.png
/usr/share/metainfo/io.github.kwadraten.patchouli.metainfo.xml
%license /usr/share/licenses/%{name}-%{version}/LICENSE

%changelog
* Fri Oct 09 2026 kwadraten <schild0van0man@duck.com> - 0.3.8-1
- Linux packaging: rpm, deb, AppImage, Flatpak
