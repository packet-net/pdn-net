using System.Net;
using AwesomeAssertions;
using Packet.Net.Routing;
using Xunit;

namespace Packet.Net.Tests;

public class CallsignResolverTests
{
    private static byte[] Ip(string s) => IPAddress.Parse(s).GetAddressBytes();

    [Fact]
    public void An_exact_host_route_resolves_to_its_callsign()
    {
        var r = CallsignResolver.FromRoutes(new[]
        {
            new RouteEntry { Ip = "44.0.0.2", Callsign = "M0LTE-10" },
        });

        r.TryResolve(Ip("44.0.0.2"), out string call).Should().BeTrue();
        call.Should().Be("M0LTE-10");
    }

    [Fact]
    public void A_cidr_route_matches_addresses_in_its_block()
    {
        var r = CallsignResolver.FromRoutes(new[]
        {
            new RouteEntry { Ip = "44.0.0.0/8", Callsign = "GB7RDG-10" },
        });

        r.TryResolve(Ip("44.130.5.9"), out string call).Should().BeTrue();
        call.Should().Be("GB7RDG-10");
    }

    [Fact]
    public void The_longest_prefix_wins_over_a_broader_route()
    {
        // Deliberately list the broad route first to prove ordering is by prefix, not input order.
        var r = CallsignResolver.FromRoutes(new[]
        {
            new RouteEntry { Ip = "44.0.0.0/8",  Callsign = "NET-8" },
            new RouteEntry { Ip = "44.0.0.0/24", Callsign = "NET-24" },
            new RouteEntry { Ip = "44.0.0.2",    Callsign = "HOST-32" },
        });

        r.TryResolve(Ip("44.0.0.2"), out string host).Should().BeTrue();
        host.Should().Be("HOST-32");

        r.TryResolve(Ip("44.0.0.9"), out string net24).Should().BeTrue();
        net24.Should().Be("NET-24");

        r.TryResolve(Ip("44.9.9.9"), out string net8).Should().BeTrue();
        net8.Should().Be("NET-8");
    }

    [Fact]
    public void A_default_route_is_the_fallback()
    {
        var r = CallsignResolver.FromRoutes(new[]
        {
            new RouteEntry { Ip = "44.0.0.0/8", Callsign = "NET" },
            new RouteEntry { Ip = "default",    Callsign = "GATEWAY" },
        });

        r.TryResolve(Ip("10.1.2.3"), out string call).Should().BeTrue();
        call.Should().Be("GATEWAY");
    }

    [Fact]
    public void No_matching_route_returns_false_so_the_packet_is_dropped()
    {
        var r = CallsignResolver.FromRoutes(new[]
        {
            new RouteEntry { Ip = "44.0.0.0/8", Callsign = "NET" },
        });

        r.TryResolve(Ip("10.1.2.3"), out string call).Should().BeFalse();
        call.Should().BeEmpty();
    }
}
