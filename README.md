# pdn-net

**A TUN/IP host stack that runs ordinary, unmodified Linux IP software over packet radio.**

`pdn-net` brings up a `pdn0` **TUN** interface and bridges IP packets ⇄ **AX.25 UI frames**
(RHPv2 `ax25`/`custom`) through a [pdn](https://github.com/packet-net/packet.net) node. Your
`ssh`, `mosh`, `mqtt`, `ping`, or home-grown UDP app opens a normal socket; the kernel routes it
out `pdn0`; pdn-net carries each IP packet to the callsign mapped to its destination address.
Nothing in the application knows it's talking over radio.

This is the **IP-seam** counterpart to [`pdn-libax25`](https://github.com/packet-net/pdn-libax25) (the native AF_AX25 seam). See
[`samples/README.md`](samples/README.md) for a worked example and a "when to use which" guide.

## How it fits

pdn-net is a **client of a pdn node over RHPv2** (loopback TCP `127.0.0.1:9000`, framed JSON). It
does **not** link packet.net's engine - like the `pdn-libax25` shim, it talks to the node over the
wire. Licence: **AGPL-3.0-or-later** (the network-source obligation follows the combined work; the
packet.net node may depend on it).

```
 ordinary IP app ──socket──> kernel ──route 44/8──> pdn0 (TUN)
                                                       │
                                              Packet.Net bridge
                                                       │  RHPv2 ax25/custom (loopback TCP:9000)
                                                       ▼
                                                   pdn node ──> radio
```

## Interoperability - standard IP-over-AX.25

**On the air, pdn-net speaks standard IP-over-AX.25, not a pdn-private framing.** Each IP packet
becomes an AX.25 **UI frame** with **PID `0xCC`** and the **raw IP datagram** in the info field -
byte-for-byte what the Linux kernel (`net/ax25/ax25_ip.c`), KA9Q NOS, JNOS and BPQ have exchanged
for decades (`0xCC` = ARPA IP, `0xCD` = ARP; datagram/UI mode). A kernel-AX.25 station in datagram
mode interoperates with pdn given matching callsign routes.

Between pdn-net and the node the PID is carried as the **first payload octet (`data[0]`)** of an
RHPv2 **`custom`** socket, with no pdn-specific wire field. The client↔node wire is therefore
**portable to any compliant RHPv2 host** ([packet.net#647](https://github.com/packet-net/packet.net/issues/647), **resolved**). This `data[0]` carriage
is G8PZT's clarification of how `custom` mode conveys a PID, not spec text: PWP-0222 §1.2 defines
`custom` only as "user specified protocol", leaving the framing to the application. None of it goes
on air; the on-air frame stays an unchanged UI frame (PID `0xCC`, raw IP in the info field).

**Invariant:** the IP datagram sits **raw** in the UI info - **no pdn envelope**. Guarded in the
bridge (`IpAx25Bridge` forwards the tun packet verbatim as the datagram `data`, PID `0xCC`).

Current interop boundaries (each a tracked follow-on - [packet.net#651](https://github.com/packet-net/packet.net/issues/651)):

- **Datagram/UI mode only** (the AMPRNet/NOS default); virtual-circuit IP (connected I-frames) is not done.
- **Static IP↔callsign routing**; dynamic AX.25-ARP (PID `0xCD`) is a follow-on - both ends need static routes until then.
- **No VJ header compression** (PIDs `0x06`/`0x07`) - plain `0xCC`; a VJ peer must disable it.
- **Small MTU (~256) + IP fragmentation** baseline; reassembling a peer's AX.25-segmented (PID `0x08`) oversize datagram is a follow-on.

Full rationale + the required on-air interop test (both directions vs the `f6fbb-on-kernel` 6.18
kernel-AX.25 VM): packet.net [`docs/network-integration-adr.md` §9](https://github.com/packet-net/packet.net/blob/main/docs/network-integration-adr.md).

## Layout

```
src/Packet.Net/          the library
  Tun/                   ITunDevice + TunDevice (/dev/net/tun P/Invoke) + fakes for tests
  Rhp/                   minimal RHPv2 ax25/custom client (framing, Latin-1 codec, socket/bind/sendto/recv)
  Routing/               PdnNetConfig + CallsignResolver (IP/CIDR → callsign, longest-prefix)
  Bridge/                IpAx25Bridge - the TUN ⇄ UI-datagram bridge
src/pdn-net/             the daemon (Program.cs): config + tun + rhp client + bridge
tests/Packet.Net.Tests/  xunit + AwesomeAssertions
samples/                 ip_udp_send.c (SPDX 0BSD) + the bring-up / "when to use which" doc
pdn-net.json             sample config (myCallsign, rhpAddress, mtu, routes)
```

## Build & test

```sh
dotnet build --configuration Release
dotnet test  --configuration Release
```

## Install (Debian/Ubuntu .deb)

Self-contained `.deb`s (bundle their own .NET runtime - no system runtime needed) for
**amd64 / arm64 / armhf** are attached to each [GitHub Release](https://github.com/packet-net/pdn-net/releases).

```sh
sudo apt install ./pdn-net_0.1.0_amd64.deb      # arm64 / armhf also published
```

The package installs the daemon under `/opt/pdn-net/app` (wrapper at `/usr/bin/pdn-net`),
an example config at `/etc/pdn-net/pdn-net.json`, and a systemd unit that ships **disabled**.
Configure it, then enable:

```sh
sudoedit /etc/pdn-net/pdn-net.json              # set myCallsign, rhpAddress, routes
sudo systemctl enable --now pdn-net             # runs as root (needs CAP_NET_ADMIN for TUN)
sudo ip addr add 44.0.0.1/32 dev pdn0           # assign this station's pdn0 address
sudo ip link set pdn0 up mtu 256
sudo ip route add 44.0.0.0/8 dev pdn0
```

The `.deb`s are built by `packaging/build-deb.sh` and released by `.github/workflows/release.yml`
(triggered by a `v*` tag). To build one locally: `packaging/build-deb.sh linux-x64 0.1.0`.

## Run (from source)

```sh
sudo setcap cap_net_admin+ep $(command -v pdn-net)   # or run as root
pdn-net --config pdn-net.json
# then, in another shell:
sudo ip addr add 44.0.0.1/32 dev pdn0 && sudo ip link set pdn0 up mtu 256
sudo ip route add 44.0.0.0/8 dev pdn0
ping 44.0.0.2      # or ssh / mosh / your app - nothing knows it's radio
```

## Status: walking skeleton

Real code, green tests, honest stubs. Known limitations / TODO (prioritised):

1. **IP fragmentation for packets > MTU** - the skeleton relies on a small interface MTU (~256) +
   path-MTU; oversize egress packets are dropped with a warning, not fragmented.
2. **ARP (PID 0xCD)** - inbound ARP is logged and ignored; a `/32` point-to-point config needs no
   ARP, but subnet-style use will.
3. **Dynamic routing** - the IP→callsign map is static config; no discovery/advertisement.
4. **Cross-platform TUN** - `TunDevice` is Linux-only (`/dev/net/tun`); macOS `utun` / Windows
   Wintun can join behind `ITunDevice`.
5. **Real-hardware bring-up** - the TUN path needs `CAP_NET_ADMIN`; end-to-end over a live node +
   radio is not yet exercised in CI (unit tests use a fake TUN + a mock RHP server).
