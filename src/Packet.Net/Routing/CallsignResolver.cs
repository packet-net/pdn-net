using System.Buffers.Binary;
using System.Net;

namespace Packet.Net.Routing;

/// <summary>
/// Resolves an outbound IPv4 destination address to the callsign that carries it, from a static
/// route table. Matching is longest-prefix: the most specific route wins, so a <c>/32</c> host
/// route overrides a <c>/8</c> net route, and a <c>default</c> route (prefix 0) is the fallback.
/// </summary>
public sealed class CallsignResolver
{
    private readonly Route[] _routes; // sorted longest-prefix first

    private readonly record struct Route(uint Network, uint Mask, int PrefixLength, string Callsign);

    private CallsignResolver(Route[] routes) => _routes = routes;

    /// <summary>Builds a resolver from a config's route entries.</summary>
    public static CallsignResolver FromConfig(PdnNetConfig config) => FromRoutes(config.Routes);

    /// <summary>Builds a resolver from a set of route entries.</summary>
    public static CallsignResolver FromRoutes(IEnumerable<RouteEntry> entries)
    {
        var list = new List<Route>();
        foreach (var e in entries)
        {
            (uint network, uint mask, int prefix) = ParseCidr(e.Ip);
            list.Add(new Route(network, mask, prefix, e.Callsign));
        }

        // Longest prefix first so the first match found is the most specific.
        list.Sort((a, b) => b.PrefixLength.CompareTo(a.PrefixLength));
        return new CallsignResolver(list.ToArray());
    }

    /// <summary>
    /// Resolves the destination callsign for an IPv4 destination address (4 bytes, network order).
    /// Returns <c>false</c> when no route matches (the packet should be dropped).
    /// </summary>
    public bool TryResolve(ReadOnlySpan<byte> destIpv4, out string callsign)
    {
        callsign = "";
        if (destIpv4.Length < 4) return false;

        uint dest = BinaryPrimitives.ReadUInt32BigEndian(destIpv4);
        foreach (var r in _routes)
        {
            if ((dest & r.Mask) == r.Network)
            {
                callsign = r.Callsign;
                return true;
            }
        }
        return false;
    }

    private static (uint Network, uint Mask, int Prefix) ParseCidr(string cidr)
    {
        cidr = cidr.Trim();
        if (cidr.Equals("default", StringComparison.OrdinalIgnoreCase))
            return (0u, 0u, 0);

        int slash = cidr.IndexOf('/');
        string addrPart = slash < 0 ? cidr : cidr[..slash];
        int prefix = slash < 0 ? 32 : int.Parse(cidr[(slash + 1)..]);
        if (prefix is < 0 or > 32)
            throw new FormatException($"Invalid CIDR prefix length in '{cidr}'.");

        if (!IPAddress.TryParse(addrPart, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new FormatException($"Invalid IPv4 address in route '{cidr}'.");

        uint addr = BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes());
        uint mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        return (addr & mask, mask, prefix);
    }
}
