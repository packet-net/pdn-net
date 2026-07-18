using System.Buffers.Binary;
using System.IO;

namespace Packet.Net.Rhp;

/// <summary>
/// RHPv2 framing: a 2-byte big-endian length prefix followed by that many bytes of UTF-8 JSON.
/// The length field is 16-bit, so a single frame is capped at 65535 bytes.
/// </summary>
/// <remarks>
/// This is the whole RHPv2 transport framing — a deliberately tiny, self-contained implementation
/// so pdn-net stays a clean RHPv2 client with no external protocol dependency. The wire format
/// matches the pdn RHP server and the MIT rhp2lib-net client byte-for-byte.
/// </remarks>
public static class RhpFraming
{
    /// <summary>Maximum payload the 16-bit length prefix can describe.</summary>
    public const int MaxPayloadLength = 0xFFFF;

    /// <summary>Writes one length-prefixed frame and flushes.</summary>
    public static async Task WriteFrameAsync(Stream output, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        if (payload.Length > MaxPayloadLength)
            throw new ArgumentException(
                $"RHP payload {payload.Length} exceeds the 16-bit frame limit ({MaxPayloadLength}).", nameof(payload));

        var header = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(header, (ushort)payload.Length);
        await output.WriteAsync(header, ct).ConfigureAwait(false);
        await output.WriteAsync(payload, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one length-prefixed frame. Returns <c>null</c> at a clean end-of-stream (zero bytes
    /// before the header); throws <see cref="EndOfStreamException"/> on a truncated frame.
    /// </summary>
    public static async Task<byte[]?> ReadFrameAsync(Stream input, CancellationToken ct = default)
    {
        var header = new byte[2];
        int first = await input.ReadAsync(header.AsMemory(0, 2), ct).ConfigureAwait(false);
        if (first == 0) return null; // clean EOS
        if (first < 2)
            await ReadExactlyAsync(input, header.AsMemory(first, 2 - first), ct).ConfigureAwait(false);

        int length = BinaryPrimitives.ReadUInt16BigEndian(header);
        if (length == 0) return Array.Empty<byte>();

        var buffer = new byte[length];
        await ReadExactlyAsync(input, buffer.AsMemory(), ct).ConfigureAwait(false);
        return buffer;
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> destination, CancellationToken ct)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int read = await stream.ReadAsync(destination.Slice(total), ct).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException(
                    $"Stream ended after {total}/{destination.Length} bytes of an RHP frame.");
            total += read;
        }
    }
}
