using System.Buffers.Binary;

namespace Packet.Net.Arp;

/// <summary>
/// AX.25-ARP packet (PID 0xCD) as used by the Linux kernel, NOS, and BPQ.
/// Layout: [htype:2][ptype:2][hlen:1][plen:1][oper:2][sha:7][spa:4][tha:7][tpa:4] = 30 bytes.
/// Hardware addresses are 7-byte shifted AX.25 callsigns; protocol addresses are IPv4.
/// </summary>
public readonly ref struct ArpPacket
{
    public const int Size = 30;
    public const ushort HTypeAx25 = 0x0003;
    public const ushort PTypeIpv4 = 0x0800;
    public const byte HLenAx25 = 7;
    public const byte PLenIpv4 = 4;
    public const ushort OpRequest = 1;
    public const ushort OpReply = 2;

    public ushort Operation { get; }
    public ReadOnlySpan<byte> SenderHw { get; }   // 7 bytes
    public ReadOnlySpan<byte> SenderProto { get; } // 4 bytes
    public ReadOnlySpan<byte> TargetHw { get; }   // 7 bytes
    public ReadOnlySpan<byte> TargetProto { get; } // 4 bytes

    private ArpPacket(ushort op, ReadOnlySpan<byte> sha, ReadOnlySpan<byte> spa, ReadOnlySpan<byte> tha, ReadOnlySpan<byte> tpa)
    {
        Operation = op;
        SenderHw = sha;
        SenderProto = spa;
        TargetHw = tha;
        TargetProto = tpa;
    }

    /// <summary>Attempts to parse an ARP packet from raw bytes (after the PID octet).</summary>
    public static bool TryParse(ReadOnlySpan<byte> data, out ArpPacket packet)
    {
        packet = default;
        if (data.Length < Size) return false;

        ushort htype = BinaryPrimitives.ReadUInt16BigEndian(data);
        ushort ptype = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        byte hlen = data[4];
        byte plen = data[5];

        if (htype != HTypeAx25 || ptype != PTypeIpv4 || hlen != HLenAx25 || plen != PLenIpv4)
            return false;

        ushort op = BinaryPrimitives.ReadUInt16BigEndian(data[6..]);
        packet = new ArpPacket(op, data.Slice(8, 7), data.Slice(15, 4), data.Slice(19, 7), data.Slice(26, 4));
        return true;
    }

    /// <summary>Builds an ARP reply from our callsign/IP to the requester.</summary>
    public static byte[] BuildReply(string ourCallsign, ReadOnlySpan<byte> ourIp, ReadOnlySpan<byte> targetHw, ReadOnlySpan<byte> targetProto)
    {
        var buf = new byte[Size];
        BinaryPrimitives.WriteUInt16BigEndian(buf, HTypeAx25);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2), PTypeIpv4);
        buf[4] = HLenAx25;
        buf[5] = PLenIpv4;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(6), OpReply);
        Ax25Address.Encode(ourCallsign).CopyTo(buf.AsSpan(8));
        ourIp.CopyTo(buf.AsSpan(15));
        targetHw.CopyTo(buf.AsSpan(19));
        targetProto.CopyTo(buf.AsSpan(26));
        return buf;
    }

    /// <summary>Builds an ARP request for a target IP (target hardware address is zeros).</summary>
    public static byte[] BuildRequest(string ourCallsign, ReadOnlySpan<byte> ourIp, ReadOnlySpan<byte> targetProto)
    {
        var buf = new byte[Size];
        BinaryPrimitives.WriteUInt16BigEndian(buf, HTypeAx25);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2), PTypeIpv4);
        buf[4] = HLenAx25;
        buf[5] = PLenIpv4;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(6), OpRequest);
        Ax25Address.Encode(ourCallsign).CopyTo(buf.AsSpan(8));
        ourIp.CopyTo(buf.AsSpan(15));
        // target hw = zeros (unknown)
        targetProto.CopyTo(buf.AsSpan(26));
        return buf;
    }
}
