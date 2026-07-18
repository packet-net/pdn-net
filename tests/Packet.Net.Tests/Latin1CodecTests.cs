using AwesomeAssertions;
using Packet.Net.Rhp;
using Xunit;

namespace Packet.Net.Tests;

public class Latin1CodecTests
{
    [Fact]
    public void Every_byte_value_survives_a_round_trip()
    {
        var all = new byte[256];
        for (int i = 0; i < 256; i++) all[i] = (byte)i;

        string wire = Latin1Codec.ToWireString(all);
        byte[] back = Latin1Codec.FromWireString(wire);

        wire.Length.Should().Be(256, "Latin-1 is one code unit per byte");
        back.Should().Equal(all);
    }

    [Fact]
    public void Control_characters_are_preserved_not_lost()
    {
        byte[] payload = { 0x00, 0x01, 0x0A, 0x0D, 0x1B, 0x7F, 0x80, 0xFF };

        byte[] back = Latin1Codec.FromWireString(Latin1Codec.ToWireString(payload));

        back.Should().Equal(payload);
    }

    [Fact]
    public void Empty_payload_maps_to_empty_string_and_back()
    {
        Latin1Codec.ToWireString(ReadOnlySpan<byte>.Empty).Should().BeEmpty();
        Latin1Codec.FromWireString(string.Empty).Should().BeEmpty();
    }

    [Fact]
    public void High_bytes_are_not_utf8_expanded()
    {
        // 0xC3 0xA9 would be "é" in UTF-8 (2 bytes → 1 char). In Latin-1 it must stay two chars.
        byte[] payload = { 0xC3, 0xA9 };

        string wire = Latin1Codec.ToWireString(payload);

        wire.Length.Should().Be(2);
        Latin1Codec.FromWireString(wire).Should().Equal(payload);
    }
}
