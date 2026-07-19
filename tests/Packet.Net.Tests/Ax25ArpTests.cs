using System.Net;
using AwesomeAssertions;
using Packet.Net.Arp;
using Packet.Net.Bridge;
using Packet.Net.Rhp;
using Packet.Net.Routing;
using Packet.Net.Tests.Fakes;
using Xunit;

namespace Packet.Net.Tests;

public class Ax25ArpTests
{
    private static readonly byte[] MyIp = IPAddress.Parse("44.0.0.1").GetAddressBytes();
    private const string MyCall = "N0CALL-10";

    private static CallsignResolver Resolver() => CallsignResolver.FromRoutes(new[]
    {
        new RouteEntry { Ip = "44.0.0.2", Callsign = "M0LTE-10" },
    });

    private static IpAx25Bridge NewBridge(FakeTunDevice tun, FakeRhpCustomClient rhp, ArpCache? cache = null)
        => new(tun, rhp, Resolver(), 256, MyCall, MyIp, cache);

    private static byte[] Framed(int pid, byte[] payload)
    {
        var framed = new byte[payload.Length + 1];
        framed[0] = (byte)pid;
        payload.CopyTo(framed, 1);
        return framed;
    }

    private static byte[] BuildArpRequest(string senderCall, byte[] senderIp, byte[] targetIp)
        => ArpPacket.BuildRequest(senderCall, senderIp, targetIp);

    [Fact]
    public async Task An_arp_request_for_our_ip_gets_a_reply()
    {
        var tun = new FakeTunDevice();
        var rhp = new FakeRhpCustomClient();
        var bridge = NewBridge(tun, rhp);

        byte[] senderIp = IPAddress.Parse("44.0.0.2").GetAddressBytes();
        byte[] arpReq = BuildArpRequest("M0LTE-10", senderIp, MyIp);
        var dg = new RhpDatagram("M0LTE-10", MyCall, Framed(IpAx25Bridge.PidArp, arpReq));

        await bridge.HandleInboundAsync(dg, CancellationToken.None);

        rhp.Sends.Should().HaveCount(1);
        var sent = rhp.Sends[0];
        sent.DestCallsign.Should().Be("M0LTE-10");
        sent.Data[0].Should().Be(IpAx25Bridge.PidArp);

        // Parse the reply
        ArpPacket.TryParse(sent.Data.AsSpan(1), out var reply).Should().BeTrue();
        reply.Operation.Should().Be(ArpPacket.OpReply);
        Ax25Address.Decode(reply.SenderHw).Should().Be(MyCall);
        reply.SenderProto.ToArray().Should().Equal(MyIp);
        reply.TargetProto.ToArray().Should().Equal(senderIp);
    }

    [Fact]
    public async Task An_arp_request_for_a_different_ip_is_not_answered()
    {
        var tun = new FakeTunDevice();
        var rhp = new FakeRhpCustomClient();
        var bridge = NewBridge(tun, rhp);

        byte[] senderIp = IPAddress.Parse("44.0.0.2").GetAddressBytes();
        byte[] otherIp = IPAddress.Parse("44.0.0.99").GetAddressBytes();
        byte[] arpReq = BuildArpRequest("M0LTE-10", senderIp, otherIp);
        var dg = new RhpDatagram("M0LTE-10", MyCall, Framed(IpAx25Bridge.PidArp, arpReq));

        await bridge.HandleInboundAsync(dg, CancellationToken.None);

        rhp.Sends.Should().BeEmpty("the request is not for our IP");
    }

    [Fact]
    public async Task An_arp_request_learns_the_senders_mapping()
    {
        var tun = new FakeTunDevice();
        var rhp = new FakeRhpCustomClient();
        var cache = new ArpCache();
        var bridge = NewBridge(tun, rhp, cache);

        byte[] senderIp = IPAddress.Parse("44.0.0.5").GetAddressBytes();
        byte[] arpReq = BuildArpRequest("G0ABC-1", senderIp, MyIp);
        var dg = new RhpDatagram("G0ABC-1", MyCall, Framed(IpAx25Bridge.PidArp, arpReq));

        await bridge.HandleInboundAsync(dg, CancellationToken.None);

        cache.TryResolve(senderIp, out string call).Should().BeTrue();
        call.Should().Be("G0ABC-1");
    }

    [Fact]
    public async Task Egress_uses_arp_cache_as_fallback_when_no_static_route()
    {
        var tun = new FakeTunDevice();
        var rhp = new FakeRhpCustomClient();
        var cache = new ArpCache();
        var bridge = NewBridge(tun, rhp, cache);

        // Learn a mapping for an IP with no static route
        byte[] destIp = IPAddress.Parse("44.0.0.50").GetAddressBytes();
        cache.Learn(destIp, "VK2XYZ-5");

        // Build an IPv4 packet destined for 44.0.0.50
        var pkt = new byte[28];
        pkt[0] = 0x45;
        destIp.CopyTo(pkt, 16);

        await bridge.ForwardOutboundAsync(pkt, CancellationToken.None);

        rhp.Sends.Should().HaveCount(1);
        rhp.Sends[0].DestCallsign.Should().Be("VK2XYZ-5");
    }

    [Fact]
    public async Task A_malformed_arp_is_ignored()
    {
        var tun = new FakeTunDevice();
        var rhp = new FakeRhpCustomClient();
        var bridge = NewBridge(tun, rhp);

        // Too short to be a valid ARP packet
        var dg = new RhpDatagram("M0LTE-10", MyCall, Framed(IpAx25Bridge.PidArp, new byte[] { 1, 2, 3 }));

        await bridge.HandleInboundAsync(dg, CancellationToken.None);

        rhp.Sends.Should().BeEmpty();
        tun.Written.Should().BeEmpty();
    }

    [Fact]
    public void Ax25Address_round_trips_callsigns()
    {
        foreach (var call in new[] { "M0LTE", "M0LTE-1", "GB7RDG-15", "N0CALL-10" })
        {
            byte[] encoded = Ax25Address.Encode(call);
            encoded.Should().HaveCount(7);
            Ax25Address.Decode(encoded).Should().Be(call);
        }
    }

    [Fact]
    public void ArpPacket_round_trips_a_request()
    {
        byte[] ourIp = { 44, 0, 0, 1 };
        byte[] targetIp = { 44, 0, 0, 2 };
        byte[] req = ArpPacket.BuildRequest("N0CALL-10", ourIp, targetIp);

        req.Should().HaveCount(ArpPacket.Size);
        ArpPacket.TryParse(req, out var arp).Should().BeTrue();
        arp.Operation.Should().Be(ArpPacket.OpRequest);
        Ax25Address.Decode(arp.SenderHw).Should().Be("N0CALL-10");
        arp.SenderProto.ToArray().Should().Equal(ourIp);
        arp.TargetProto.ToArray().Should().Equal(targetIp);
    }
}
