using System.Text.Json;
using System.Text.Json.Serialization;

namespace Packet.Net.Routing;

/// <summary>
/// The pdn-net daemon configuration, loaded from <c>pdn-net.json</c>.
/// </summary>
public sealed class PdnNetConfig
{
    /// <summary>This node's callsign — bound as the local (source) address on the custom socket.</summary>
    [JsonPropertyName("myCallsign")]
    public string MyCallsign { get; init; } = "N0CALL";

    /// <summary>
    /// This node's IPv4 address on the TUN interface (e.g. "44.0.0.1"). Used to answer inbound
    /// AX.25-ARP requests. Optional: if unset, ARP replies are not sent.
    /// </summary>
    [JsonPropertyName("myIp")]
    public string? MyIp { get; init; }

    /// <summary>The pdn node's RHPv2 endpoint as <c>host:port</c> (default <c>127.0.0.1:9000</c>).</summary>
    [JsonPropertyName("rhpAddress")]
    public string RhpAddress { get; init; } = "127.0.0.1:9000";

    /// <summary>The TUN interface name to create (default <c>pdn0</c>).</summary>
    [JsonPropertyName("tunName")]
    public string TunName { get; init; } = "pdn0";

    /// <summary>
    /// The interface MTU to advertise/enforce, in bytes. Kept small (~256) for a byte-starved radio
    /// channel; the bridge drops any IP packet larger than this (fragmentation is a TODO).
    /// </summary>
    [JsonPropertyName("mtu")]
    public int Mtu { get; init; } = 256;

    /// <summary>The IP→callsign route table (order-independent; longest-prefix wins at lookup).</summary>
    [JsonPropertyName("routes")]
    public IReadOnlyList<RouteEntry> Routes { get; init; } = Array.Empty<RouteEntry>();

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Parses a configuration from JSON text.</summary>
    public static PdnNetConfig Parse(string json)
        => JsonSerializer.Deserialize<PdnNetConfig>(json, Options)
           ?? throw new InvalidOperationException("pdn-net config parsed to null.");

    /// <summary>Loads a configuration from a JSON file on disk.</summary>
    public static PdnNetConfig Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>Splits <see cref="RhpAddress"/> into host and port (default port 9000).</summary>
    public (string Host, int Port) RhpEndpoint()
    {
        int colon = RhpAddress.LastIndexOf(':');
        if (colon < 0) return (RhpAddress, 9000);
        string host = RhpAddress[..colon];
        return int.TryParse(RhpAddress[(colon + 1)..], out int port) ? (host, port) : (host, 9000);
    }
}

/// <summary>A single IP→callsign route. <see cref="Ip"/> is a plain address (implicit /32) or CIDR.</summary>
public sealed class RouteEntry
{
    /// <summary>An IPv4 address (<c>44.0.0.2</c>), a CIDR (<c>44.0.0.0/8</c>), or <c>default</c>/<c>0.0.0.0/0</c>.</summary>
    [JsonPropertyName("ip")]
    public string Ip { get; init; } = "";

    /// <summary>The destination callsign IP traffic to <see cref="Ip"/> is sent to.</summary>
    [JsonPropertyName("callsign")]
    public string Callsign { get; init; } = "";
}
