#!/bin/sh
# deb-install-smoke.sh — prove a built pdn-net .deb installs cleanly on a pristine
# base. The pdn-net analogue of packet.net's headend-deb-install-smoke.sh.
#
#   packaging/deb-install-smoke.sh <path-to.deb> [image ...]
#   e.g. packaging/deb-install-smoke.sh artifacts/pdn-net_0.1.0_amd64.deb
#
# For each base image (default: Debian-stable + Ubuntu-LTS) it runs a THROWAWAY
# container and asserts, on a bare base:
#   1. `apt install ./pkg.deb` resolves the declared Depends + installs (exit 0).
#   2. dpkg state is 'installed'; the apphost + /usr/bin wrapper + unit + example
#      config landed; the unit's ExecStart is correct and it ships DISABLED
#      (postinst created NO enable symlink); the TUN wiring (User=root,
#      AmbientCapabilities=CAP_NET_ADMIN, DeviceAllow=/dev/net/tun) is present.
#   3. the self-contained binary actually RUNS on this base (loads the bundled .NET
#      runtime, parses the config, prints its startup banner) — proving no missing
#      shared lib. It then exits non-zero because a bare container has no
#      CAP_NET_ADMIN / /dev/net/tun, which is the expected graceful failure.
#   4. `apt purge` removes the payload AND the conffile.
#
# If docker is unavailable this SKIPS with a warning (exit 0) rather than failing —
# the release workflow must not be blocked by a docker-less runner.
set -eu

[ $# -ge 1 ] || { echo "usage: $0 <path-to.deb> [image ...]" >&2; exit 2; }
DEB_PATH=$(cd "$(dirname "$1")" && pwd)/$(basename "$1"); shift
[ -f "$DEB_PATH" ] || { echo "no such .deb: $DEB_PATH" >&2; exit 2; }
[ $# -ge 1 ] && IMAGES="$*" || IMAGES="debian:stable-slim ubuntu:24.04"

if ! command -v docker >/dev/null 2>&1; then
  echo "::warning::docker not available — skipping the .deb install-smoke (the .deb was still built)."
  exit 0
fi

DEB_DIR=$(dirname "$DEB_PATH")
DEB_BASE=$(basename "$DEB_PATH")

# The assertions, run inside the container. Fully single-quoted: every $VAR here is
# expanded by the container's /bin/sh. The .deb basename arrives via -e DEB.
INNER='
set -u
fail() { echo "SMOKE_FAIL: $1"; exit 1; }
cd /work

echo "== 1. apt install (resolves Depends from the repo) =="
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq                          || fail "apt-get update"
apt-get install -y "./$DEB"                  || fail "apt install ./$DEB (non-zero)"

echo "== 2. dpkg state + payload + unit wiring =="
dpkg -s pdn-net 2>/dev/null | grep -q "^Status: install ok installed" || fail "dpkg status != installed"
[ -x /opt/pdn-net/app/pdn-net ]                    || fail "apphost missing / not executable"
[ -L /usr/bin/pdn-net ]                            || fail "/usr/bin/pdn-net wrapper symlink missing"
[ -x /usr/bin/pdn-net ]                            || fail "/usr/bin/pdn-net not executable via symlink"
[ -f /lib/systemd/system/pdn-net.service ]         || fail "systemd unit missing"
[ -f /etc/pdn-net/pdn-net.json ]                   || fail "example config missing"
grep -q "^ExecStart=/usr/bin/pdn-net --config /etc/pdn-net/pdn-net.json$" /lib/systemd/system/pdn-net.service \
  || fail "unit ExecStart wrong"
grep -q "^User=root" /lib/systemd/system/pdn-net.service \
  || fail "unit missing User=root"
grep -Eq "^AmbientCapabilities=.*CAP_NET_ADMIN" /lib/systemd/system/pdn-net.service \
  || fail "unit missing AmbientCapabilities=CAP_NET_ADMIN (TUN would be denied)"
grep -Eq "^DeviceAllow=/dev/net/tun" /lib/systemd/system/pdn-net.service \
  || fail "unit missing DeviceAllow=/dev/net/tun"
grep -q "^WantedBy=multi-user.target" /lib/systemd/system/pdn-net.service \
  || fail "unit missing [Install] WantedBy (operator could not enable it)"
# DISABLED by default: postinst must NOT have created an enable symlink.
[ ! -e /etc/systemd/system/multi-user.target.wants/pdn-net.service ] \
  || fail "unit was auto-enabled on install (must ship disabled)"
echo "  ok: installed; apphost+wrapper+unit+config present; ExecStart+TUN wiring correct; ships disabled"

echo "== 3. self-contained binary RUNS on this base (loads .NET, parses config, banners) =="
# No /dev/net/tun and no CAP_NET_ADMIN in a bare container, so it fails gracefully
# after the banner — that non-zero exit is expected; we only need proof it LOADED.
set +e
OUT=$(/usr/bin/pdn-net --config /etc/pdn-net/pdn-net.json 2>&1)
RC=$?
set -e
printf "%s\n" "$OUT" | sed "s/^/    /"
printf "%s" "$OUT" | grep -q "pdn-net starting" \
  || fail "binary did not print its startup banner (self-contained runtime failed to load?)"
[ "$RC" -ne 139 ] && [ "$RC" -ne 132 ] && [ "$RC" -ne 127 ] \
  || fail "binary crashed (rc=$RC) — a shared lib is probably missing from Depends"
echo "  ok: bundled .NET runtime loaded, config parsed, banner printed (rc=$RC as expected on a base with no TUN)"

echo "== 4. purge removes payload + conffile =="
apt-get purge -y pdn-net >/dev/null 2>&1   || fail "apt purge (non-zero)"
[ -e /opt/pdn-net/app/pdn-net ]            && fail "payload survived purge"
[ -e /etc/pdn-net/pdn-net.json ]           && fail "conffile survived purge"
echo "  ok: payload + conffile removed"

echo "INNER_PASS"
'

rc=0
for img in $IMAGES; do
  echo "############################## $img ##############################"
  if docker run --rm \
       -e DEB="$DEB_BASE" \
       -v "$DEB_DIR":/work:ro \
       "$img" sh -c "$INNER"; then
    echo ">>> $img: PASS"
  else
    echo ">>> $img: FAIL"
    rc=1
  fi
done

[ "$rc" = 0 ] && echo "SMOKE_PASS (all images)" || echo "SMOKE_FAIL (one or more images)"
exit "$rc"
