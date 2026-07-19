using System.Collections.Concurrent;

namespace Packet.Net.Bridge;

/// <summary>
/// Reassembles AX.25-segmented datagrams (PID 0x08) per the AX.25 spec §6.3. A peer (kernel-AX.25,
/// NOS, BPQ) may segment an oversize UI payload into multiple frames, each carrying PID 0x08
/// followed by a 1-byte segment header: bits 7–1 = segment number (0-based), bit 0 = 1 on the last
/// segment. Segments arrive in order; the reassembler buffers them keyed by source callsign and
/// delivers the complete payload once the last segment arrives.
/// </summary>
public sealed class Ax25Reassembler
{
    private readonly ConcurrentDictionary<string, Pending> _pending = new();
    private readonly int _maxSegments;

    /// <param name="maxSegments">Safety cap on buffered segments per source (default 16).</param>
    public Ax25Reassembler(int maxSegments = 16) => _maxSegments = maxSegments;

    /// <summary>
    /// Feeds one segment's info field (after the PID 0x08 octet). Returns the reassembled payload
    /// (without any segment headers) when the last segment arrives, or null if more are expected.
    /// Returns null and discards state on protocol violations (out-of-order, too many segments).
    /// </summary>
    public byte[]? AddSegment(string source, ReadOnlySpan<byte> info)
    {
        if (info.IsEmpty) return null;

        byte segHeader = info[0];
        int segNo = segHeader >> 1;
        bool last = (segHeader & 1) != 0;
        ReadOnlySpan<byte> data = info[1..];

        var pending = _pending.GetOrAdd(source, _ => new Pending());

        lock (pending)
        {
            if (segNo != pending.Segments.Count)
            {
                // Out-of-order or duplicate — discard and reset.
                _pending.TryRemove(source, out _);
                return null;
            }

            if (pending.Segments.Count >= _maxSegments)
            {
                _pending.TryRemove(source, out _);
                return null;
            }

            pending.Segments.Add(data.ToArray());

            if (!last) return null;

            // Last segment — concatenate and deliver.
            _pending.TryRemove(source, out _);
            int total = 0;
            foreach (var seg in pending.Segments) total += seg.Length;
            var result = new byte[total];
            int pos = 0;
            foreach (var seg in pending.Segments)
            {
                seg.CopyTo(result, pos);
                pos += seg.Length;
            }
            return result;
        }
    }

    private sealed class Pending
    {
        public List<byte[]> Segments { get; } = new();
    }
}
