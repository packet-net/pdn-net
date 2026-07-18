# pdn-net samples — ordinary IP software over packet radio

`ip_udp_send.c` is a deliberately boring program: a plain `socket(AF_INET, SOCK_DGRAM)` that
`sendto()`s one UDP datagram to an IP address you pass on the command line. It has **zero**
AX.25 or packet-radio awareness. It works over the radio only because `pdn-net` has brought up
a `pdn0` TUN interface and your routing table sends the destination's subnet to it.

That is the entire point of the tun/IP seam: **any existing IP program works unmodified.**

## Build the sample

```sh
cc -Wall -Wextra -o ip_udp_send ip_udp_send.c
```

## Bring up `pdn0` and route 44-net over it

`pdn-net` opens `/dev/net/tun`, which needs `CAP_NET_ADMIN`. Either run it as root, or grant the
capability to the published binary once:

```sh
sudo setcap cap_net_admin+ep /path/to/pdn-net      # then run it as your user
# ...or just: sudo /path/to/pdn-net --config pdn-net.json
```

Start the daemon (it prints the interface name, the bound callsign, and the RHP connection state):

```sh
pdn-net --config pdn-net.json
```

Then, in another shell, give the interface an address, bring it up, and route 44-net (AMPRNet)
to it. Pick your own 44-net IP for the interface; the MTU stays small for a byte-starved channel:

```sh
sudo ip addr add 44.0.0.1/32 dev pdn0
sudo ip link set pdn0 up mtu 256
sudo ip route add 44.0.0.0/8 dev pdn0
```

Now run the sample against a 44-net peer whose IP your `pdn-net.json` maps to a callsign:

```sh
./ip_udp_send 44.0.0.2 5555 "hello over radio"
```

The datagram leaves as an AX.25 UI frame (PID 0xCC) to the mapped callsign. **Or literally just
`ping 44.0.0.2` / `ssh 44.0.0.2` / `mosh 44.0.0.2`** — nothing on your machine knows it's radio.
It's just IP going out an interface that happens to be a modem on the other side.

---

## Native (pdn-libax25) vs tun/IP (pdn-net) — when to use which

pdn exposes **two seams** to ordinary software, and they are for genuinely different jobs.

**Native AF_AX25 shim (`pdn-libax25`).** Your application addresses a **callsign** and speaks
connected-mode AX.25 (or UI) directly — the way a BBS, `axcall`, `axlisten`, node software, and
the rest of the Linux AX.25 ecosystem already work. There is **no IP overhead**: a frame carries
your bytes and a pair of callsigns, nothing more. This is the right seam for anything that is
*natively about callsigns and packet* — connecting to a BBS, running a keyboard-to-keyboard chat,
NET/ROM, APRS-adjacent tooling. The application already thinks in callsigns, so you hand it the
callsign world unchanged.

**tun/IP (`pdn-net`, this repo).** Your application addresses an **IP** and uses ordinary
sockets. The headline benefit is that **any existing IP software works with no changes at all** —
`ssh`, `mosh`, `mqtt`, `git`, `ping`, your own UDP tool — because it just opens a socket and the
kernel routes the packet out `pdn0`. The costs are real and worth stating plainly: you pay
**IP + UDP/TCP header overhead** on a channel where every byte is expensive; you maintain a
**callsign ↔ IP map** (the `routes` in `pdn-net.json`); and delivery is **best-effort UI
datagrams** — there is no link-layer ARQ under you, so end-to-end reliability is the app's job
(TCP's own retransmit, or an app-level scheme). At 300–9600 baud the realistically usable set is
**small, latency-tolerant IP**: `mosh` and `ssh`, MQTT, tiny REST/JSON, your own datagram apps —
**not** the web, TLS-heavy services, or anything chatty.

**Rule of thumb:** if your program knows about **callsigns**, use the **native shim**; if it only
knows **IP**, use **tun**.

For the full rationale (the "two seams" design, the UI-datagram default, and the addressing/routing
model), see `packet.net/docs/network-integration-adr.md`.
