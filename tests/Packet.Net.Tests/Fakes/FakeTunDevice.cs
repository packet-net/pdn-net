using System.Threading.Channels;
using Packet.Net.Tun;

namespace Packet.Net.Tests.Fakes;

/// <summary>
/// An in-memory <see cref="ITunDevice"/> for tests: the test enqueues packets that "arrive" from
/// the interface (egress direction) and inspects packets the bridge writes back (ingress direction).
/// No kernel device, no privileges — this is what lets CI exercise the bridge without root.
/// </summary>
public sealed class FakeTunDevice : ITunDevice
{
    private readonly Channel<byte[]> _toRead = Channel.CreateUnbounded<byte[]>();

    /// <summary>Packets the bridge wrote to the interface, in order (verbatim copies).</summary>
    public List<byte[]> Written { get; } = new();

    /// <inheritdoc/>
    public string Name => "faketun0";

    /// <summary>Queues a packet to be returned by the next <see cref="ReadPacketAsync"/>.</summary>
    public void EnqueueForRead(byte[] packet) => _toRead.Writer.TryWrite(packet);

    /// <summary>Signals end-of-stream so a pump loop reading this device exits.</summary>
    public void CompleteReads() => _toRead.Writer.TryComplete();

    /// <inheritdoc/>
    public async ValueTask<int> ReadPacketAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        try
        {
            byte[] packet = await _toRead.Reader.ReadAsync(ct).ConfigureAwait(false);
            packet.CopyTo(buffer);
            return packet.Length;
        }
        catch (ChannelClosedException)
        {
            return 0; // EOS
        }
    }

    /// <inheritdoc/>
    public ValueTask WritePacketAsync(ReadOnlyMemory<byte> packet, CancellationToken ct = default)
    {
        Written.Add(packet.ToArray());
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public void Dispose() => _toRead.Writer.TryComplete();
}
