namespace Packet.Net.Tun;

/// <summary>
/// A layer-3 TUN network interface: reads and writes whole IP packets, one packet per call.
/// </summary>
/// <remarks>
/// The interface exists so the bridge and its tests never touch <c>/dev/net/tun</c> directly.
/// CI (and every unit test) uses an in-memory fake; only the daemon on a real host opens the
/// kernel device (<see cref="TunDevice"/>), which needs <c>CAP_NET_ADMIN</c> / root.
/// </remarks>
public interface ITunDevice : IDisposable
{
    /// <summary>The kernel-assigned interface name (e.g. <c>pdn0</c>).</summary>
    string Name { get; }

    /// <summary>
    /// Reads exactly one IP packet from the interface into <paramref name="buffer"/> and returns
    /// its length in bytes. A TUN read yields one packet per call (no stream framing). Returns 0
    /// at end-of-stream (the device was closed).
    /// </summary>
    ValueTask<int> ReadPacketAsync(Memory<byte> buffer, CancellationToken ct = default);

    /// <summary>Writes one whole IP packet to the interface (delivered to the local IP stack).</summary>
    ValueTask WritePacketAsync(ReadOnlyMemory<byte> packet, CancellationToken ct = default);
}
