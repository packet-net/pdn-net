using AwesomeAssertions;
using Packet.Net.Rhp;
using Xunit;

namespace Packet.Net.Tests;

public class RhpFramingTests
{
    [Fact]
    public async Task A_written_frame_reads_back_identically()
    {
        byte[] payload = System.Text.Encoding.UTF8.GetBytes("{\"type\":\"socket\"}");
        using var ms = new MemoryStream();

        await RhpFraming.WriteFrameAsync(ms, payload);
        ms.Position = 0;
        byte[]? back = await RhpFraming.ReadFrameAsync(ms);

        back.Should().Equal(payload);
    }

    [Fact]
    public async Task The_length_prefix_is_two_bytes_big_endian()
    {
        byte[] payload = new byte[0x0102];
        using var ms = new MemoryStream();

        await RhpFraming.WriteFrameAsync(ms, payload);

        byte[] framed = ms.ToArray();
        framed[0].Should().Be(0x01);
        framed[1].Should().Be(0x02);
        framed.Length.Should().Be(2 + 0x0102);
    }

    [Fact]
    public async Task Clean_end_of_stream_returns_null()
    {
        using var ms = new MemoryStream();
        (await RhpFraming.ReadFrameAsync(ms)).Should().BeNull();
    }

    [Fact]
    public async Task A_payload_over_the_16bit_limit_is_rejected()
    {
        var payload = new byte[RhpFraming.MaxPayloadLength + 1];
        using var ms = new MemoryStream();

        Func<Task> act = async () => await RhpFraming.WriteFrameAsync(ms, payload);

        await act.Should().ThrowAsync<ArgumentException>();
    }
}
