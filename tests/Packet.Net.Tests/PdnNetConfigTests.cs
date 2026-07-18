using AwesomeAssertions;
using Packet.Net.Routing;
using Xunit;

namespace Packet.Net.Tests;

public class PdnNetConfigTests
{
    [Fact]
    public void Parses_the_sample_config_shape()
    {
        const string json = """
        {
          "myCallsign": "N0CALL-10",
          "rhpAddress": "127.0.0.1:9000",
          "tunName": "pdn0",
          "mtu": 256,
          "routes": [
            { "ip": "44.0.0.2",   "callsign": "M0LTE-10" },
            { "ip": "44.0.0.0/8", "callsign": "GB7RDG-10" }
          ]
        }
        """;

        var config = PdnNetConfig.Parse(json);

        config.MyCallsign.Should().Be("N0CALL-10");
        config.TunName.Should().Be("pdn0");
        config.Mtu.Should().Be(256);
        config.Routes.Should().HaveCount(2);
        config.Routes[0].Ip.Should().Be("44.0.0.2");
        config.Routes[0].Callsign.Should().Be("M0LTE-10");
    }

    [Theory]
    [InlineData("127.0.0.1:9000", "127.0.0.1", 9000)]
    [InlineData("192.168.1.5:9100", "192.168.1.5", 9100)]
    [InlineData("localhost", "localhost", 9000)]
    public void Splits_the_rhp_endpoint_into_host_and_port(string address, string host, int port)
    {
        var config = PdnNetConfig.Parse($$"""{ "rhpAddress": "{{address}}" }""");

        var (h, p) = config.RhpEndpoint();

        h.Should().Be(host);
        p.Should().Be(port);
    }
}
