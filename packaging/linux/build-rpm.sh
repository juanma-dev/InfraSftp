#!/bin/bash
# Builds a signed binary RPM for InfraSftp on Fedora.
#
# Run this from a Fedora host with the project sources on a real Linux
# filesystem (not /mnt/c — see CLAUDE.md). It does:
#   1. Reads <Version> from InfraSftp.csproj.
#   2. Runs `dotnet publish` for net8.0 / linux-x64 self-contained.
#   3. Extracts PNG icons from Assets/avalonia-logo.ico via icotool.
#   4. Stages publish/, the .desktop, the wrapper, and the icons into
#      ~/rpmbuild/SOURCES/infrasftp-<ver>.tar.gz.
#   5. Templates the version into a copy of the spec file.
#   6. Runs rpmbuild -bb.
#   7. If GPG_NAME is exported, signs the resulting RPM with
#      `rpmsign --addsign` and verifies the signature.
#
# Required Fedora packages (install once):
#   sudo dnf install rpm-build rpm-sign rpmdevtools icoutils
#
# Optional environment variables:
#   GPG_NAME   GPG uid to sign with (e.g. "InfraSftp Release Signing").
#              When unset, the RPM is built unsigned.
#   OUT_DIR    Where to copy the final RPM (default: <repo>/releases-linux).
set -euo pipefail

# Resolve repo root from this script's location.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
cd "$REPO_ROOT"

CSPROJ="$REPO_ROOT/InfraSftp.csproj"
VERSION="$(grep -oP '(?<=<Version>)[^<]+' "$CSPROJ" | head -1)"
if [[ -z "$VERSION" ]]; then
    echo "ERROR: could not read <Version> from $CSPROJ" >&2
    exit 1
fi

OUT_DIR="${OUT_DIR:-$REPO_ROOT/releases-linux}"
RPMTOP="$HOME/rpmbuild"
STAGE_NAME="infrasftp-$VERSION"
STAGE_DIR="$(mktemp -d)/$STAGE_NAME"

echo "==> Building InfraSftp $VERSION RPM"
echo "    repo:   $REPO_ROOT"
echo "    out:    $OUT_DIR"
echo "    sign:   ${GPG_NAME:-NO}"
echo

# 1. Set up rpmbuild tree.
rpmdev-setuptree

# 2. Publish self-contained.
echo "==> dotnet publish (Release, linux-x64, self-contained)..."
PUBLISH_DIR="$STAGE_DIR/publish"
mkdir -p "$PUBLISH_DIR"
dotnet publish "$CSPROJ" \
    -c Release \
    -f net8.0 \
    -r linux-x64 \
    --self-contained true \
    -p:PublishSingleFile=false \
    -o "$PUBLISH_DIR" \
    /p:Version="$VERSION" \
    --nologo

# 3. Extract icons from .ico.
echo "==> Extracting PNG icons..."
ICON_TMP="$(mktemp -d)"
icotool -x -o "$ICON_TMP" "$REPO_ROOT/Assets/avalonia-logo.ico" 2>/dev/null
mkdir -p "$STAGE_DIR/icons"
for size in 16 24 32 48 64 96 128 256; do
    src="$(ls "$ICON_TMP"/*_${size}x${size}x*.png 2>/dev/null | head -1 || true)"
    if [[ -z "$src" ]]; then
        echo "WARN: no $size px layer in .ico, will derive from largest" >&2
        src="$(ls "$ICON_TMP"/*.png | sort -r | head -1)"
        convert "$src" -resize "${size}x${size}" "$STAGE_DIR/icons/${size}.png"
    else
        cp "$src" "$STAGE_DIR/icons/${size}.png"
    fi
done
rm -rf "$ICON_TMP"

# 4. Stage launcher + desktop entry + license + readme.
cp "$SCRIPT_DIR/infrasftp.wrapper"  "$STAGE_DIR/infrasftp"
cp "$SCRIPT_DIR/infrasftp.desktop"  "$STAGE_DIR/infrasftp.desktop"
cp "$REPO_ROOT/LICENSE"             "$STAGE_DIR/LICENSE"
cp "$REPO_ROOT/README.md"           "$STAGE_DIR/README.md"
chmod 0755 "$STAGE_DIR/infrasftp"

# 5. Tar it up into rpmbuild SOURCES.
echo "==> Creating source tarball..."
TARBALL="$RPMTOP/SOURCES/$STAGE_NAME.tar.gz"
tar -czf "$TARBALL" -C "$(dirname "$STAGE_DIR")" "$STAGE_NAME"
ls -la "$TARBALL"

# 6. Drop a version-stamped spec into SPECS.
SPEC_OUT="$RPMTOP/SPECS/infrasftp.spec"
sed "s/%{version}/$VERSION/g" "$SCRIPT_DIR/infrasftp.spec" > "$SPEC_OUT"

# 7. Build.
echo "==> rpmbuild -bb..."
rpmbuild -bb \
    --define "_topdir $RPMTOP" \
    "$SPEC_OUT" 2>&1 | tail -30

# 8. Locate built RPM.
RPM_FILE="$(ls -t "$RPMTOP/RPMS/x86_64/infrasftp-$VERSION-"*.rpm 2>/dev/null | head -1)"
if [[ -z "$RPM_FILE" || ! -f "$RPM_FILE" ]]; then
    echo "ERROR: rpmbuild did not produce an RPM" >&2
    exit 1
fi
echo
echo "==> Built: $RPM_FILE"

# 9. Sign if GPG_NAME is set.
if [[ -n "${GPG_NAME:-}" ]]; then
    echo "==> Signing with GPG identity: $GPG_NAME"
    rpmsign --addsign --define "_gpg_name $GPG_NAME" "$RPM_FILE"
    echo "==> Verifying signature..."
    rpm --checksig -v "$RPM_FILE" || true
fi

# 10. Copy to OUT_DIR for the caller.
mkdir -p "$OUT_DIR"
cp "$RPM_FILE" "$OUT_DIR/"
echo
echo "DONE. RPM in $OUT_DIR/$(basename "$RPM_FILE")"
