using System.Net;
using AwesomeAssertions;
using Packet.Net.Bridge;
using Packet.Net.Rhp;
using Packet.Net.Routing;
using Packet.Net.Tests.Fakes;
using Xunit;

namespace Packet.Net.Tests;

public class IpAx25BridgeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static CallsignResolver Resolver() => CallsignResolver.FromRoutes(new[]
    {
        new RouteEntry { Ip = "44.0.0.2",   Callsign = "M0LTE-10" },
        new RouteEntry { Ip = "44.0.0.0/8", Callsign = "GB7RDG-10" },
    });

    // A minimal-but-valid-enough IPv4 packet: version nibble 4, dest at bytes 16..19, a recognisable
    // payload pattern so we can assert the exact bytes crossed the bridge unchanged.
    private static byte[] Ipv4(string dest, int length = 28)
    {
        var pkt = new byte[length];
        pkt[0] = 0x45; // IPv4, IHL 5
        for (int i = 20; i < length; i++) pkt[i] = (byte)(i * 7);
        IPAddress.Parse(dest).GetAddressBytes().CopyTo(pkt, 16);
        return pkt;
    }

    private static IpAx25Bridge NewBridge(FakeTunDevice tun, FakeRhpDgramClient rhp, int mtu = 256)
        => new(tun, rhp, Resolver(), mtu);

    [Fact]
    public async Task An_ipv4_packet_egresses_as_a_sendto_to_the_resolved_callsign_with_pid_0xCC()
    {
        var tun = new FakeTunDevice();
        var rhp = new FakeRhpDgramClient();
        var bridge = NewBridge(tun, rhp);

        byte[] packet = Ipv4("44.0.0.2");
        await bridge.ForwardOutboundAsync(packet, CancellationToken.None);

        rhp.Sends.Should().HaveCount(1);
        var sent = rhp.Sends[0];
        sent.DestCallsign.Should().Be("M0LTE-10");
        sent.Pid.Should().Be(IpAx25Bridge.PidIp);
        sent.Data.Should().Equal(packet, "the IP bytes must cross verbatim");
    }

    [Fact]
    public async Task The_tun_read_loop_forwards_a_queued_packet()
    {
        var tun = new FakeTunDevice();
        var rhp = new FakeRhpDgramClient();
        var bridge = NewBridge(tun, rhp);

        byte[] packet = Ipv4("44.130.1.1"); // matches 44.0.0.0/8 → GB7RDG-10
        tun.EnqueueForRead(packet);
        tun.CompleteReads(); // loop exits at EOS

        await bridge.PumpOutboundAsync(CancellationToken.None);

        rhp.Sends.Should().HaveCount(1);
        rhp.Sends[0].DestCallsign.Should().Be("GB7RDG-10");
        rhp.Sends[0].Data.Should().Equal(packet);
    }

    [Fact]
    public async Task An_inbound_ip_datagram_is_written_back_to_the_tun_verbatim()
    {
        var tun = new FakeTunDevice();
        var rhp = new FakeRhpDgramClient();
        var bridge = NewBridge(tun, rhp);

        byte[] ipBytes = Ipv4("44.0.0.9");
        await bridge.HandleInboundAsync(new RhpDatagram("GB7RDG-10", "N0CALL-10", IpAx25Bridge.PidIp, ipBytes), CancellationToken.None);

        tun.Written.Should().HaveCount(1);
        tun.Written[0].Should().Equal(ipBytes);
    }

    [Fact]
    public async Task A_running_bridge_wires_inbound_pushes_through_to_the_tun()
    {
        var tun = new FakeTunDevice();
        var rhp = new FakeRhpDgramClient();
        var bridge = NewBridge(tun, rhp);
        using var cts = new CancellationTokenSource(Timeout);

        Task run = bridge.RunAsync(cts.Token);

        byte[] ipBytes = Ipv4("44.0.0.2");
        rhp.InjectInbound(new RhpDatagram("M0LTE-10", "N0CALL-10", IpAx25Bridge.PidIp, ipBytes));

        await WaitUntil(() => tun.Written.Count == 1, cts.Token);
        tun.Written[0].Should().Equal(ipBytes);

        tun.CompleteReads(); // let the pump loop exit
        await run;
    }

    [Fact]
    public async Task A_packet_with_no_matching_route_is_dropped()
    {
        var tun = new FakeTunDevice();
        var rhp = new FakeRhpDgramClient();
        var bridge = NewBridge(tun, rhp);

        await bridge.ForwardOutboundAsync(Ipv4("10.1.2.3"), CancellationToken.None);

        rhp.Sends.Should().BeEmpty();
    }

    [Fact]
    public async Task An_oversize_packet_is_dropped_pending_fragmentation()
    {
        var tun = new FakeTunDevice();
        var rhp = new FakeRhpDgramClient();
        var bridge = NewBridge(tun, rhp, mtu: 64);

        await bridge.ForwardOutboundAsync(Ipv4("44.0.0.2", length: 200), CancellationToken.None);

        rhp.Sends.Should().BeEmpty();
    }

    [Fact]
    public async Task A_non_ipv4_packet_is_ignored()
    {
        var tun = new FakeTunDevice();
        var rhp = new FakeRhpDgramClient();
        var bridge = NewBridge(tun, rhp);

        byte[] v6 = new byte[40];
        v6[0] = 0x60; // version 6

        await bridge.ForwardOutboundAsync(v6, CancellationToken.None);

        rhp.Sends.Should().BeEmpty();
    }

    [Fact]
    public async Task An_inbound_arp_datagram_is_not_written_to_the_tun()
    {
        var tun = new FakeTunDevice();
        var rhp = new FakeRhpDgramClient();
        var bridge = NewBridge(tun, rhp);

        await bridge.HandleInboundAsync(new RhpDatagram("M0LTE-10", "N0CALL-10", IpAx25Bridge.PidArp, new byte[] { 1, 2, 3 }), CancellationToken.None);

        tun.Written.Should().BeEmpty("ARP (0xCD) handling is a TODO");
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
