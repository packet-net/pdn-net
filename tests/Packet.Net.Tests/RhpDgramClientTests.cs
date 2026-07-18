using AwesomeAssertions;
using Packet.Net.Rhp;
using Packet.Net.Tests.Fakes;
using Xunit;

namespace Packet.Net.Tests;

public class RhpDgramClientTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Sendto_reaches_the_server_with_the_exact_bytes_via_latin1()
    {
        await using var server = MockRhpDgramServer.Start();
        await using var client = new RhpDgramClient("127.0.0.1", server.Port);
        using var cts = new CancellationTokenSource(Timeout);

        await client.ConnectAsync(cts.Token);
        await client.BindAsync("N0CALL-10", cts.Token);

        // A payload spanning the full byte range, including control chars and high bytes, to prove
        // the Latin-1 (not base64, not UTF-8) transport survives the JSON round-trip.
        var payload = new byte[256];
        for (int i = 0; i < 256; i++) payload[i] = (byte)i;

        await client.SendUiAsync("M0LTE-10", 0xCC, payload, cts.Token);

        // Poll briefly for the async capture on the server side.
        await WaitUntil(() => server.Sends.Count == 1, cts.Token);

        var sent = server.Sends[0];
        sent.Remote.Should().Be("M0LTE-10");
        sent.Local.Should().Be("N0CALL-10", "the bound callsign becomes the sendto source");
        sent.Pid.Should().Be(0xCC);
        sent.Data.Should().Equal(payload);
    }

    [Fact]
    public async Task An_inbound_recv_push_is_surfaced_verbatim()
    {
        await using var server = MockRhpDgramServer.Start();
        await using var client = new RhpDgramClient("127.0.0.1", server.Port);
        using var cts = new CancellationTokenSource(Timeout);

        var received = new TaskCompletionSource<RhpDatagram>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.DatagramReceived += dg => received.TrySetResult(dg);

        await client.ConnectAsync(cts.Token);
        await client.BindAsync("N0CALL-10", cts.Token);

        byte[] data = { 0x45, 0x00, 0x00, 0x14, 0xDE, 0xAD, 0xBE, 0xEF, 0xFF, 0x00 };
        await server.PushRecvAsync(source: "M0LTE-10", dest: "N0CALL-10", pid: 0xCC, data);

        RhpDatagram dg = await received.Task.WaitAsync(cts.Token);

        dg.Source.Should().Be("M0LTE-10");
        dg.Dest.Should().Be("N0CALL-10");
        dg.Pid.Should().Be(0xCC);
        dg.Data.ToArray().Should().Equal(data);
    }

    [Fact]
    public async Task Sendto_before_bind_is_rejected()
    {
        await using var server = MockRhpDgramServer.Start();
        await using var client = new RhpDgramClient("127.0.0.1", server.Port);
        using var cts = new CancellationTokenSource(Timeout);

        await client.ConnectAsync(cts.Token);

        Func<Task> act = async () => await client.SendUiAsync("M0LTE-10", 0xCC, new byte[] { 1 }, cts.Token);

        await act.Should().ThrowAsync<RhpException>();
    }

    private static async Task WaitUntil(Func<bool> condition, CancellationToken ct)
    {
        while (!condition())
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(10, ct);
        }
    }
}
