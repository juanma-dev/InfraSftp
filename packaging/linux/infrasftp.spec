# RPM spec for InfraSftp on Fedora.
#
# This is a *binary* RPM: we ship the self-contained .NET 8 publish
# output verbatim under /opt/infrasftp and only declare the runtime
# system dependencies the app shells out to (libsecret) or relies on
# for glyph coverage (DejaVu, Noto Emoji). The .NET runtime is bundled,
# so dotnet is not a Requires.
#
# Build flow:
#   1. dotnet publish -f net8.0 -c Release -r linux-x64 \
#                     --self-contained true -p:PublishSingleFile=false
#      -> writes ~ 100 MB of managed + native deps into a directory.
#   2. packaging/linux/build-rpm.sh stages that directory plus the
#      wrapper script, the .desktop entry and the extracted icons into
#      a tarball under ~/rpmbuild/SOURCES/, then invokes rpmbuild -bb
#      against this spec.
#
# Why /opt and not /usr/lib: a self-contained .NET app drops ~ 200
# loose .so / .dll files that would pollute /usr/lib if installed
# there, and the .NET deps don't follow standard SONAME conventions
# (so AutoReqProv would be wrong). /opt/<vendor> is the FHS-blessed
# location for self-contained third-party app bundles, which is exactly
# what this is.

# Self-contained .NET ships pre-built native deps; there are no source
# files to attach to a debuginfo package, so opt out of the auto-split
# that Fedora's rpm macros perform by default. Without this, rpmbuild
# fails with "Empty %files file ... debugsourcefiles.list".
%define debug_package %{nil}
%global __os_install_post %{nil}
%global _build_id_links none

Name:           infrasftp
Version:        %{version}
Release:        1%{?dist}
Summary:        Two-pane SFTP client (Avalonia)

License:        GPL-3.0-or-later
URL:            https://github.com/juanma-dev/InfraSftp
Source0:        %{name}-%{version}.tar.gz

BuildArch:      x86_64

# Suppress auto-detection of provides/requires inside /opt/infrasftp.
# The bundled .NET native libraries would otherwise be scanned for
# SONAMEs and produce unsatisfiable auto-Requires (libhostfxr.so etc.)
# that no system package owns.
AutoReqProv:    no

# libsecret: the LibsecretVault password backend shells out to
#   secret-tool from this package.
# dejavu-sans-fonts + google-noto-emoji-fonts: the app uses Inter for
#   Latin text but falls back to system fonts for symbols / emoji
#   glyphs in toolbar buttons and tab close icons. Without these the
#   buttons render as tofu (empty squares) on a minimal Fedora.
Requires:       libsecret
Requires:       dejavu-sans-fonts
Requires:       google-noto-emoji-fonts

%description
InfraSftp is a two-pane SFTP client for Linux and Windows, inspired
by WinSCP and FileZilla. Saved connection profiles, side-by-side
local/remote browsers, drag-and-drop transfers, and rsync-style
skip-on-match. Passwords are stored via libsecret in the active
desktop session keyring.

%prep
%setup -q

%build
# Nothing to compile: we ship the pre-built self-contained publish
# output. The %build phase is intentionally empty.

%install
rm -rf %{buildroot}

# 1. Application payload under /opt/infrasftp
install -d %{buildroot}/opt/%{name}
cp -a publish/. %{buildroot}/opt/%{name}/
chmod 0755 %{buildroot}/opt/%{name}/InfraSftp

# 2. Launcher in /usr/bin
install -d %{buildroot}%{_bindir}
install -m 0755 infrasftp %{buildroot}%{_bindir}/%{name}

# 3. Desktop entry
install -d %{buildroot}%{_datadir}/applications
install -m 0644 infrasftp.desktop %{buildroot}%{_datadir}/applications/%{name}.desktop

# 4. Hicolor icons (one PNG per resolution)
for size in 16 24 32 48 64 96 128 256; do
    install -d %{buildroot}%{_datadir}/icons/hicolor/${size}x${size}/apps
    install -m 0644 icons/${size}.png \
        %{buildroot}%{_datadir}/icons/hicolor/${size}x${size}/apps/%{name}.png
done

%post
# Refresh the icon cache so the new sizes light up without a logout.
# Failure is non-fatal: the desktop will still find the icon, just
# with a one-cycle delay.
if [ -x /usr/bin/gtk-update-icon-cache ]; then
    /usr/bin/gtk-update-icon-cache --quiet /usr/share/icons/hicolor || :
fi

%postun
if [ $1 -eq 0 ] && [ -x /usr/bin/gtk-update-icon-cache ]; then
    /usr/bin/gtk-update-icon-cache --quiet /usr/share/icons/hicolor || :
fi

%files
%license LICENSE
%doc README.md
/opt/%{name}/
%{_bindir}/%{name}
%{_datadir}/applications/%{name}.desktop
%{_datadir}/icons/hicolor/*/apps/%{name}.png

%changelog
* Wed Apr 29 2026 juanma-dev <johnimmanuelx@gmail.com> - 0.2.0-1
- First Linux release. Multi-target net8.0 + net8.0-windows.
- Password vault on Linux backed by libsecret / Secret Service.
- Bundles the .NET 8 runtime; only system deps are libsecret and
  the DejaVu / Noto Emoji font packages.
