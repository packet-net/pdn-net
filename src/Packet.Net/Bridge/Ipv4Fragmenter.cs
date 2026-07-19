using System.Buffers.Binary;

namespace Packet.Net.Bridge;

/// <summary>
/// Splits an oversize IPv4 datagram into RFC 791 fragments, each no larger than a given MTU.
/// The original header's identification, flags, and fragment offset fields are rewritten per
/// fragment. Options (if any) are replicated in every fragment per the RFC.
/// </summary>
public static class Ipv4Fragmenter
{
    /// <summary>
    /// Fragments <paramref name="packet"/> into datagrams each at most <paramref name="mtu"/> bytes.
    /// Returns the original packet unchanged if it already fits. Each returned array is a complete
    /// IPv4 datagram ready for transmission.
    /// </summary>
    public static IReadOnlyList<byte[]> Fragment(ReadOnlySpan<byte> packet, int mtu)
    {
        if (packet.Length <= mtu)
            return new[] { packet.ToArray() };

        int ihl = (packet[0] & 0x0F) * 4;
        if (ihl < 20 || ihl > packet.Length)
            return new[] { packet.ToArray() };

        int totalLen = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);
        if (totalLen < ihl || totalLen > packet.Length) totalLen = packet.Length;

        ReadOnlySpan<byte> header = packet[..ihl];
        ReadOnlySpan<byte> payload = packet.Slice(ihl, totalLen - ihl);

        // Fragment payload must be a multiple of 8 (except the last).
        int maxPayloadPerFrag = (mtu - ihl) & ~7;
        if (maxPayloadPerFrag < 8)
            return new[] { packet.ToArray() };

        var fragments = new List<byte[]>();
        int offset = 0;
        ushort id = BinaryPrimitives.ReadUInt16BigEndian(packet[4..]);

        while (offset < payload.Length)
        {
            int chunkLen = Math.Min(maxPayloadPerFrag, payload.Length - offset);
            bool last = offset + chunkLen >= payload.Length;

            var frag = new byte[ihl + chunkLen];
            header.CopyTo(frag);

            // Total length
            BinaryPrimitives.WriteUInt16BigEndian(frag.AsSpan(2), (ushort)(ihl + chunkLen));

            // Flags + fragment offset (bytes 6..7): offset is in 8-byte units.
            ushort flagsAndOffset = (ushort)(offset / 8);
            if (!last)
                flagsAndOffset |= 0x2000; // MF (More Fragments) bit
            BinaryPrimitives.WriteUInt16BigEndian(frag.AsSpan(6), flagsAndOffset);

            // Recompute header checksum.
            BinaryPrimitives.WriteUInt16BigEndian(frag.AsSpan(10), 0);
            BinaryPrimitives.WriteUInt16BigEndian(frag.AsSpan(10), Checksum(frag.AsSpan(0, ihl)));

            payload.Slice(offset, chunkLen).CopyTo(frag.AsSpan(ihl));
            fragments.Add(frag);
            offset += chunkLen;
        }

        return fragments;
    }

    private static ushort Checksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        for (int i = 0; i < data.Length - 1; i += 2)
            sum += (uint)(data[i] << 8 | data[i + 1]);
        if ((data.Length & 1) != 0)
            sum += (uint)(data[^1] << 8);
        while (sum >> 16 != 0)
            sum = (sum & 0xFFFF) + (sum >> 16);
        return (ushort)~sum;
    }
}
