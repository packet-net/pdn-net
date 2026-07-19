using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Net.Rhp;
using Packet.Net.Routing;
using Packet.Net.Tun;

namespace Packet.Net.Bridge;

/// <summary>
/// The heart of pdn-net: bridges IP packets on a TUN interface to and from AX.25 UI datagrams on a
/// pdn node.
/// <list type="bullet">
/// <item><b>Outbound</b>: read a packet from the TUN → parse the IPv4 destination (header bytes
/// 16–19) → resolve a callsign → <c>sendto(callsign, [0xCC] ++ ipBytes)</c> on the custom socket.</item>
/// <item><b>Inbound</b>: an RHPv2 <c>recv</c> whose <c>data[0]</c> is PID 0xCC → strip the PID octet
/// and write the IP bytes back to the TUN.</item>
/// </list>
/// The AX.25 PID rides as the first payload octet (<c>data[0]</c>) per RHPv2 <c>custom</c> mode; the
/// on-air framing is unchanged (a UI frame, PID 0xCC, raw IP info field).
/// No IP stack is involved: only the destination address is read out of the header; the local
/// kernel does the rest once the packet reaches the interface.
/// </summary>
public sealed class IpAx25Bridge
{
    /// <summary>AX.25 PID for IP (RFC 1226 / the AX.25 spec) — the PID pdn-net rides.</summary>
    public const int PidIp = 0xCC;

    /// <summary>AX.25 PID for ARP. Not handled yet (TODO): a /32 point-to-point config needs no ARP.</summary>
    public const int PidArp = 0xCD;

    private const int MinIpv4HeaderLength = 20;

    private readonly ITunDevice _tun;
    private readonly IRhpCustomClient _rhp;
    private readonly CallsignResolver _resolver;
    private readonly int _maxPacketBytes;
    private readonly ILogger _log;

    /// <summary>Creates the bridge over an open TUN device and a bound RHP custom client.</summary>
    /// <param name="tun">The TUN interface.</param>
    /// <param name="rhp">A connected + bound RHP custom client.</param>
    /// <param name="resolver">The IP→callsign resolver.</param>
    /// <param name="maxPacketBytes">
    /// Largest IP packet accepted on egress. A larger packet is dropped (fragmentation is a TODO);
    /// keep the interface MTU at or below this. Also bounds the TUN read buffer.
    /// </param>
    /// <param name="log">Optional logger.</param>
    public IpAx25Bridge(
        ITunDevice tun,
        IRhpCustomClient rhp,
        CallsignResolver resolver,
        int maxPacketBytes = 256,
        ILogger<IpAx25Bridge>? log = null)
    {
        _tun = tun;
        _rhp = rhp;
        _resolver = resolver;
        _maxPacketBytes = Math.Max(maxPacketBytes, MinIpv4HeaderLength);
        _log = log ?? NullLogger<IpAx25Bridge>.Instance;
    }

    /// <summary>
    /// Runs the bridge until <paramref name="ct"/> is cancelled: subscribes to inbound datagrams and
    /// pumps the TUN read loop. Returns when the loop ends (cancellation or the TUN closing).
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        _rhp.DatagramReceived += OnDatagramReceived;
        try
        {
            await PumpOutboundAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _rhp.DatagramReceived -= OnDatagramReceived;
        }
    }

    /// <summary>
    /// The TUN→RHP loop, exposed for tests to drive one buffer size. Reads packets from the TUN and
    /// forwards each via <see cref="ForwardOutboundAsync"/>.
    /// </summary>
    public async Task PumpOutboundAsync(CancellationToken ct)
    {
        // One read buffer sized to the packet ceiling — TUN yields one packet per read.
        var buffer = new byte[_maxPacketBytes + 4];
        while (!ct.IsCancellationRequested)
        {
            int n;
            try
            {
                n = await _tun.ReadPacketAsync(buffer, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            if (n == 0) break; // TUN closed

            await ForwardOutboundAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Forwards a single outbound IP packet: validate IPv4 → resolve dest callsign → sendto with the
    /// PID 0xCC octet prepended (<c>data = [0xCC] ++ ipBytes</c>). Drops (with a log line) on
    /// non-IPv4, over-MTU, or no matching route. Exposed for tests.
    /// </summary>
    public async Task ForwardOutboundAsync(ReadOnlyMemory<byte> packet, CancellationToken ct)
    {
        ReadOnlySpan<byte> span = packet.Span;
        if (span.Length < MinIpv4HeaderLength)
        {
            _log.LogDebug("Dropping runt packet ({Length} bytes)", span.Length);
            return;
        }

        int version = span[0] >> 4;
        if (version != 4)
        {
            // IPv6 (0xCC still, but no v6 routing table yet) and anything else are out of scope here.
            _log.LogDebug("Dropping non-IPv4 packet (version {Version})", version);
            return;
        }

        if (span.Length > _maxPacketBytes)
        {
            // TODO: IP fragmentation for packets larger than one UI frame. For the skeleton we rely
            // on a small interface MTU + path-MTU discovery; oversize egress is dropped, not split.
            _log.LogWarning(
                "Dropping oversize IP packet ({Length} > {Max} bytes) — fragmentation not implemented",
                span.Length, _maxPacketBytes);
            return;
        }

        // IPv4 destination address = header bytes 16..19 (no options parsing needed). Resolve and
        // format everything off the span *before* the await — a Span cannot cross an await boundary.
        ReadOnlySpan<byte> dest = span.Slice(16, 4);
        string destText = FormatIp(dest);
        int length = span.Length;
        if (!_resolver.TryResolve(dest, out string callsign))
        {
            _log.LogDebug("No route for {Dest} — dropping", destText);
            return;
        }

        // custom mode: the PID rides as data[0], so prepend 0xCC to the raw IP datagram. The on-air
        // UI framing is unchanged — this is only how the local client↔node RHP wire carries the PID.
        var framed = new byte[length + 1];
        framed[0] = (byte)PidIp;
        packet.Span.CopyTo(framed.AsSpan(1));

        await _rhp.SendAsync(callsign, framed, ct).ConfigureAwait(false);
        _log.LogDebug("→ {Callsign} pid 0x{Pid:X2} ({Length} bytes) for {Dest}",
            callsign, PidIp, length, destText);
    }

    /// <summary>
    /// Handles one inbound datagram. In custom mode the PID is <c>data[0]</c>: PID 0xCC has its PID
    /// octet stripped and <c>data[1..]</c> written verbatim to the TUN; 0xCD (ARP) and other PIDs are
    /// logged and ignored. An empty datagram is dropped. Exposed for tests.
    /// </summary>
    public async Task HandleInboundAsync(RhpDatagram dg, CancellationToken ct)
    {
        if (dg.Data.IsEmpty)
        {
            _log.LogDebug("Ignoring empty datagram from {Source}", dg.Source);
            return;
        }

        int pid = dg.Data.Span[0];
        switch (pid)
        {
            case PidIp:
                // Strip the PID octet; the IP datagram is data[1..].
                ReadOnlyMemory<byte> ip = dg.Data[1..];
                await _tun.WritePacketAsync(ip, ct).ConfigureAwait(false);
                _log.LogDebug("← {Source} pid 0x{Pid:X2} ({Length} bytes)", dg.Source, pid, ip.Length);
                break;
            case PidArp:
                // TODO: answer ARP (0xCD) so a subnet route (not just /32) works over tun.
                _log.LogDebug("Ignoring ARP (pid 0xCD) from {Source} — not implemented", dg.Source);
                break;
            default:
                _log.LogDebug("Ignoring pid 0x{Pid:X2} from {Source}", pid, dg.Source);
                break;
        }
    }

    private void OnDatagramReceived(RhpDatagram dg)
    {
        // The RHP read loop raises this synchronously; hand off without blocking it. Errors are
        // swallowed to a log line so one bad packet never tears down the bridge.
        _ = Task.Run(async () =>
        {
            try
            {
                await HandleInboundAsync(dg, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Inbound datagram from {Source} failed", dg.Source);
            }
        });
    }

    private static string FormatIp(ReadOnlySpan<byte> b) => $"{b[0]}.{b[1]}.{b[2]}.{b[3]}";
}
