# pdn-net

**A TUN/IP host stack that runs ordinary, unmodified Linux IP software over packet radio.**

`pdn-net` brings up a `pdn0` **TUN** interface and bridges IP packets ⇄ **AX.25 UI frames**
(RHPv2 `ax25`/`dgram`) through a [pdn](https://github.com/packet-net/packet.net) node. Your
`ssh`, `mosh`, `mqtt`, `ping`, or home-grown UDP app opens a normal socket; the kernel routes it
out `pdn0`; pdn-net carries each IP packet to the callsign mapped to its destination address.
Nothing in the application knows it's talking over radio.

This is the **IP-seam** counterpart to `pdn-libax25` (the native AF_AX25 seam). See
[`samples/README.md`](samples/README.md) for a worked example and a "when to use which" guide.

## How it fits

pdn-net is a **client of a pdn node over RHPv2** (loopback TCP `127.0.0.1:9000`, framed JSON). It
does **not** link packet.net's engine — like the `pdn-libax25` shim, it talks to the node over the
wire. Licence: **AGPL-3.0-or-later** (the network-source obligation follows the combined work; the
packet.net node may depend on it).

```
 ordinary IP app ──socket──> kernel ──route 44/8──> pdn0 (TUN)
                                                       │
                                              Packet.Net bridge
                                                       │  RHPv2 ax25/dgram (loopback TCP:9000)
                                                       ▼
                                                   pdn node ──> radio
```

## Layout

```
src/Packet.Net/          the library
  Tun/                   ITunDevice + TunDevice (/dev/net/tun P/Invoke) + fakes for tests
  Rhp/                   minimal RHPv2 ax25/dgram client (framing, Latin-1 codec, socket/bind/sendto/recv)
  Routing/               PdnNetConfig + CallsignResolver (IP/CIDR → callsign, longest-prefix)
  Bridge/                IpAx25Bridge — the TUN ⇄ UI-datagram bridge
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

## Run

```sh
sudo setcap cap_net_admin+ep $(command -v pdn-net)   # or run as root
pdn-net --config pdn-net.json
# then, in another shell:
sudo ip addr add 44.0.0.1/32 dev pdn0 && sudo ip link set pdn0 up mtu 256
sudo ip route add 44.0.0.0/8 dev pdn0
ping 44.0.0.2      # or ssh / mosh / your app — nothing knows it's radio
```

## Status: walking skeleton

Real code, green tests, honest stubs. Known limitations / TODO (prioritised):

1. **IP fragmentation for packets > MTU** — the skeleton relies on a small interface MTU (~256) +
   path-MTU; oversize egress packets are dropped with a warning, not fragmented.
2. **ARP (PID 0xCD)** — inbound ARP is logged and ignored; a `/32` point-to-point config needs no
   ARP, but subnet-style use will.
3. **Dynamic routing** — the IP→callsign map is static config; no discovery/advertisement.
4. **Cross-platform TUN** — `TunDevice` is Linux-only (`/dev/net/tun`); macOS `utun` / Windows
   Wintun can join behind `ITunDevice`.
5. **Real-hardware bring-up** — the TUN path needs `CAP_NET_ADMIN`; end-to-end over a live node +
   radio is not yet exercised in CI (unit tests use a fake TUN + a mock RHP server).
