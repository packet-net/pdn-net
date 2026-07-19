#!/usr/bin/env bash
#
# build-deb.sh — publish pdn-net (the TUN/IP host daemon) self-contained for one
# RID and package it as a Debian .deb. Used locally and by .github/workflows/release.yml.
# The pdn-net analogue of packet.net's scripts/build-deb.sh.
#
#   packaging/build-deb.sh <rid> <version>
#   e.g. packaging/build-deb.sh linux-arm64 0.1.0
#
# Cross-publishes self-contained from x64 (no ReadyToRun), so all three arches
# build on one x64 host with no arch-native machine and no cross C-toolchain — the
# .NET runtime pack for each RID is restored from NuGet. Produces
# artifacts/pdn-net_<version>_<arch>.deb.
set -euo pipefail

rid="${1:?usage: build-deb.sh <rid> <version>}"
version="${2:?usage: build-deb.sh <rid> <version>}"

# rid -> dpkg arch.
case "$rid" in
  linux-x64)   arch=amd64 ;;
  linux-arm64) arch=arm64 ;;
  linux-arm)   arch=armhf ;;
  *) echo "unknown rid: $rid (want linux-x64 | linux-arm64 | linux-arm)" >&2; exit 2 ;;
esac

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
pkg="$root/packaging"
proj="$root/src/pdn-net/pdn-net.csproj"
pub="$root/artifacts/publish/$rid"
stage="$root/artifacts/deb/$rid"
out="$root/artifacts/pdn-net_${version}_${arch}.deb"

command -v dotnet >/dev/null 2>&1 || { echo "dotnet not found (need the .NET 10 SDK)" >&2; exit 2; }

echo "==> publish $rid (self-contained, invariant globalization, no R2R)"
rm -rf "$pub"
# InvariantGlobalization is already set in Directory.Build.props (no libicu dependency).
# Not single-file: the apphost resolves its DLLs from its own directory via /proc/self/exe,
# so a /usr/bin symlink to it works and there is no runtime self-extraction step.
dotnet publish "$proj" -c Release -r "$rid" --self-contained true \
  -p:Version="$version" \
  -p:PublishSingleFile=false \
  -p:DebugType=none -p:DebugSymbols=false \
  -v minimal -o "$pub"

[ -x "$pub/pdn-net" ] || { echo "publish produced no pdn-net apphost at $pub/pdn-net" >&2; exit 1; }

echo "==> stage .deb tree for $arch"
rm -rf "$stage"
install -d "$stage/opt/pdn-net/app" "$stage/usr/bin" \
          "$stage/lib/systemd/system" "$stage/etc/pdn-net" "$stage/DEBIAN"

# The self-contained publish tree (apphost + runtime + app DLLs).
cp -a "$pub/." "$stage/opt/pdn-net/app/"
# Normalise perms: the publish output carries group-write + stray +x bits (umask
# artefacts). Make dirs 0755 and files 0644, then the apphost 0755. The bundled
# *.so libs are dlopen'd (need read, not exec), so 0644 is correct for them too.
find "$stage/opt/pdn-net/app" -type d -exec chmod 0755 {} +
find "$stage/opt/pdn-net/app" -type f -exec chmod 0644 {} +
chmod 0755 "$stage/opt/pdn-net/app/pdn-net"

# /usr/bin/pdn-net -> the apphost. Absolute symlink (crosses top-level dirs, so
# Debian policy wants it absolute — a relative one would trip lintian).
ln -sf /opt/pdn-net/app/pdn-net "$stage/usr/bin/pdn-net"

# systemd unit — DISABLED by default (postinst never enables/starts it).
install -m 0644 "$pkg/pdn-net.service" "$stage/lib/systemd/system/pdn-net.service"
grep -q '^ExecStart=/usr/bin/pdn-net --config /etc/pdn-net/pdn-net.json$' \
  "$stage/lib/systemd/system/pdn-net.service" \
  || { echo "unit ExecStart is not the expected /usr/bin/pdn-net line" >&2; exit 1; }

# Example config, tracked as a dpkg conffile (operator edits survive upgrades).
install -m 0644 "$pkg/pdn-net.json.example" "$stage/etc/pdn-net/pdn-net.json"

# DEBIAN metadata.
sed -e "s/@ARCH@/$arch/" -e "s/@VERSION@/$version/" \
    "$pkg/control.in" > "$stage/DEBIAN/control"
install -m 0755 "$pkg/postinst" "$pkg/prerm" "$pkg/postrm" "$stage/DEBIAN/"
install -m 0644 "$pkg/conffiles" "$stage/DEBIAN/conffiles"

echo "==> build .deb"
mkdir -p "$root/artifacts"
# --root-owner-group (dpkg >= 1.19): root:root files without fakeroot.
dpkg-deb --build --root-owner-group "$stage" "$out"

echo "==> built $out"
dpkg-deb --info "$out"
# Diagnostics only — disable pipefail so a head/grep closing a pipe early can't abort.
set +o pipefail
echo "--- payload (top) ---"
dpkg-deb --contents "$out" | awk '{print $1, $6}' | head -15
echo "--- unit + wrapper + conffile ---"
dpkg-deb --contents "$out" | grep -E 'pdn-net.service|usr/bin/pdn-net|etc/pdn-net/pdn-net.json'
echo "--- conffiles (must list the config) ---"
dpkg-deb --info "$out" | grep -iA2 'conffiles' || echo "    (WARNING: no Conffiles!)"
set -o pipefail
if command -v lintian >/dev/null 2>&1; then
  lintian "$out" || true
else
  echo "(lintian not installed — skipping deb-lint)"
fi
