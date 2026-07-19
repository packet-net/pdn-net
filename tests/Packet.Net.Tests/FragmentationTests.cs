using System.Buffers.Binary;
using System.Net;
using AwesomeAssertions;
using Packet.Net.Bridge;
using Packet.Net.Rhp;
using Packet.Net.Routing;
using Packet.Net.Tests.Fakes;
using Xunit;

namespace Packet.Net.Tests;

public class FragmentationTests
{
    private static byte[] Ipv4(string dest, int length = 28)
    {
        var pkt = new byte[length];
        pkt[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(pkt.AsSpan(2), (ushort)length);
        for (int i = 20; i < length; i++) pkt[i] = (byte)(i * 7);
        IPAddress.Parse(dest).GetAddressBytes().CopyTo(pkt, 16);
        return pkt;
    }

    [Fact]
    public void A_packet_within_mtu_is_returned_unchanged()
    {
        byte[] pkt = Ipv4("44.0.0.2", 100);
        var frags = Ipv4Fragmenter.Fragment(pkt, 256);
        frags.Should().HaveCount(1);
        frags[0].Should().Equal(pkt);
    }

    [Fact]
    public void An_oversize_packet_is_split_into_valid_fragments()
    {
        byte[] pkt = Ipv4("44.0.0.2", 500);
        var frags = Ipv4Fragmenter.Fragment(pkt, 100);

        frags.Should().HaveCountGreaterThan(1);
        foreach (var frag in frags)
            frag.Length.Should().BeLessThanOrEqualTo(100);

        // First fragment: MF set, offset 0
        ushort flags0 = BinaryPrimitives.ReadUInt16BigEndian(frags[0].AsSpan(6));
        (flags0 & 0x2000).Should().NotBe(0, "MF must be set on non-last fragments");
        (flags0 & 0x1FFF).Should().Be(0, "first fragment offset is 0");

        // Last fragment: MF clear
        ushort flagsLast = BinaryPrimitives.ReadUInt16BigEndian(frags[^1].AsSpan(6));
        (flagsLast & 0x2000).Should().Be(0, "MF must be clear on last fragment");

        // All fragments share the same identification
        ushort id0 = BinaryPrimitives.ReadUInt16BigEndian(frags[0].AsSpan(4));
        foreach (var frag in frags)
            BinaryPrimitives.ReadUInt16BigEndian(frag.AsSpan(4)).Should().Be(id0);

        // Payload reassembly: concatenate all fragment payloads in offset order
        var reassembled = new List<byte>();
        foreach (var frag in frags)
        {
            int ihl = (frag[0] & 0x0F) * 4;
            int offset = (BinaryPrimitives.ReadUInt16BigEndian(frag.AsSpan(6)) & 0x1FFF) * 8;
            while (reassembled.Count < offset + (frag.Length - ihl))
                reassembled.Add(0);
            for (int i = 0; i < frag.Length - ihl; i++)
                reassembled[offset + i] = frag[ihl + i];
        }

        // Compare with original payload
        byte[] originalPayload = pkt[20..];
        reassembled.Should().Equal(originalPayload);
    }

    [Fact]
    public void Fragment_payloads_are_8_byte_aligned_except_last()
    {
        byte[] pkt = Ipv4("44.0.0.2", 300);
        var frags = Ipv4Fragmenter.Fragment(pkt, 80);

        for (int i = 0; i < frags.Count - 1; i++)
        {
            int ihl = (frags[i][0] & 0x0F) * 4;
            int payloadLen = frags[i].Length - ihl;
            (payloadLen % 8).Should().Be(0, "non-last fragment payloads must be 8-byte aligned");
        }
    }

    [Fact]
    public async Task Inbound_ax25_segments_are_reassembled()
    {
        var tun = new FakeTunDevice();
        var rhp = new FakeRhpCustomClient();
        var resolver = CallsignResolver.FromRoutes(new[]
        {
            new RouteEntry { Ip = "44.0.0.2", Callsign = "M0LTE-10" },
        });
        var bridge = new IpAx25Bridge(tun, rhp, resolver, 256);

        // Simulate a 3-segment AX.25 payload: "Hello, world!" split across segments.
        // Segment header: (segNo << 1) | lastBit
        byte[] part1 = { 0x48, 0x65, 0x6c, 0x6c }; // "Hell"
        byte[] part2 = { 0x6f, 0x2c, 0x20, 0x77 }; // "o, w"
        byte[] part3 = { 0x6f, 0x72, 0x6c, 0x64 }; // "orld"

        byte[] seg1 = new byte[1 + part1.Length]; seg1[0] = (0 << 1) | 0; part1.CopyTo(seg1, 1);
        byte[] seg2 = new byte[1 + part2.Length]; seg2[0] = (1 << 1) | 0; part2.CopyTo(seg2, 1);
        byte[] seg3 = new byte[1 + part3.Length]; seg3[0] = (2 << 1) | 1; part3.CopyTo(seg3, 1); // last

        byte[] Frame(byte[] seg) { var f = new byte[seg.Length + 1]; f[0] = IpAx25Bridge.PidSegment; seg.CopyTo(f, 1); return f; }

        await bridge.HandleInboundAsync(new RhpDatagram("M0LTE-10", "N0CALL", Frame(seg1)), CancellationToken.None);
        tun.Written.Should().BeEmpty("not yet complete");

        await bridge.HandleInboundAsync(new RhpDatagram("M0LTE-10", "N0CALL", Frame(seg2)), CancellationToken.None);
        tun.Written.Should().BeEmpty("not yet complete");

        await bridge.HandleInboundAsync(new RhpDatagram("M0LTE-10", "N0CALL", Frame(seg3)), CancellationToken.None);
        tun.Written.Should().HaveCount(1);
        tun.Written[0].Should().Equal("Hello, world"u8.ToArray());
    }

    [Fact]
    public void Reassembler_discards_out_of_order_segments()
    {
        var reasm = new Ax25Reassembler();

        // Send segment 1 before segment 0 — should be discarded
        byte[] seg1 = { (1 << 1) | 0, 0xAA };
        reasm.AddSegment("SRC", seg1).Should().BeNull();

        // Now segment 0 — starts fresh
        byte[] seg0 = { (0 << 1) | 0, 0xBB };
        reasm.AddSegment("SRC", seg0).Should().BeNull();

        // Segment 1 again — completes
        byte[] seg1b = { (1 << 1) | 1, 0xCC };
        byte[]? result = reasm.AddSegment("SRC", seg1b);
        result.Should().NotBeNull();
        result.Should().Equal(new byte[] { 0xBB, 0xCC });
    }
}
