using System.Collections.Concurrent;

namespace Packet.Net.Arp;

/// <summary>
/// A thread-safe cache of IP→callsign mappings learned from ARP traffic. Entries expire after a
/// configurable TTL. Used as a fallback by the bridge when no static route matches a destination.
/// </summary>
public sealed class ArpCache
{
    private readonly ConcurrentDictionary<uint, Entry> _entries = new();
    private readonly TimeSpan _ttl;

    private readonly record struct Entry(string Callsign, long ExpiresTicks);

    /// <param name="ttl">Time-to-live for learned entries. Default 10 minutes.</param>
    public ArpCache(TimeSpan? ttl = null) => _ttl = ttl ?? TimeSpan.FromMinutes(10);

    /// <summary>Records or refreshes an IP→callsign mapping.</summary>
    public void Learn(ReadOnlySpan<byte> ipv4, string callsign)
    {
        if (ipv4.Length < 4 || string.IsNullOrEmpty(callsign)) return;
        uint key = ReadU32(ipv4);
        long expires = Environment.TickCount64 + (long)_ttl.TotalMilliseconds;
        _entries[key] = new Entry(callsign, expires);
    }

    /// <summary>Looks up a callsign for a destination IP. Returns false if unknown or expired.</summary>
    public bool TryResolve(ReadOnlySpan<byte> ipv4, out string callsign)
    {
        callsign = "";
        if (ipv4.Length < 4) return false;
        uint key = ReadU32(ipv4);
        if (!_entries.TryGetValue(key, out var entry)) return false;
        if (Environment.TickCount64 > entry.ExpiresTicks)
        {
            _entries.TryRemove(key, out _);
            return false;
        }
        callsign = entry.Callsign;
        return true;
    }

    private static uint ReadU32(ReadOnlySpan<byte> b) =>
        (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
}
