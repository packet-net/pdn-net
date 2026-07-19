using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Packet.Net.Rhp;

/// <summary>
/// A minimal RHPv2 <c>ax25</c>/<c>custom</c> client over loopback TCP. It speaks only the datagram
/// subset pdn-net needs — <c>socket</c> / <c>bind</c> / <c>sendto</c> plus inbound <c>recv</c> —
/// as a framed-JSON TCP client. This keeps pdn-net a clean, self-contained RHPv2 client with no
/// external protocol dependency; the MIT rhp2lib-net client speaks the same wire and could be
/// substituted behind <see cref="IRhpCustomClient"/> if a full RHP surface were ever wanted here.
/// </summary>
/// <remarks>
/// Wire contract (pdn rhp2-server, the <c>ax25</c>/<c>custom</c> seam). In <c>custom</c> mode the
/// AX.25 PID is the first payload octet (<c>data[0]</c>) — there is no separate <c>pid</c> field:
/// <list type="bullet">
/// <item><c>socket{pfam:"ax25",mode:"custom"}</c> → reply carries a <c>handle</c>.</item>
/// <item><c>bind{handle,local:&lt;callsign&gt;,port:null}</c>.</item>
/// <item><c>sendto{handle,remote:&lt;dest&gt;,local:&lt;source&gt;,data:&lt;latin1&gt;}</c> where <c>data[0]</c> is the PID.</item>
/// <item>inbound <c>recv</c> pushes carry <c>remote</c> (source) / <c>local</c> (dest) / <c>data</c> (PID = <c>data[0]</c>).</item>
/// <item>replies echo the request <c>id</c>; async pushes carry a <c>seqno</c> and no <c>id</c>.</item>
/// <item><c>data</c> is Latin-1, one byte per code unit (never base64).</item>
/// </list>
/// </remarks>
public sealed class RhpCustomClient : IRhpCustomClient
{
    /// <summary>The default RHPv2 TCP port.</summary>
    public const int DefaultPort = 9000;

    private readonly string _host;
    private readonly int _port;
    private readonly ILogger _log;

    private readonly ConcurrentDictionary<int, TaskCompletionSource<RhpReply>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private TcpClient? _tcp;
    private Stream? _stream;
    private Task? _reader;
    private CancellationTokenSource? _readerCts;
    private int _nextId;
    private int _handle;
    private string? _localCallsign;
    private int _disposed;

    /// <inheritdoc/>
    public event Action<RhpDatagram>? DatagramReceived;

    /// <summary>Creates a client for the node at <paramref name="host"/>:<paramref name="port"/>.</summary>
    public RhpCustomClient(string host = "127.0.0.1", int port = DefaultPort, ILogger<RhpCustomClient>? log = null)
    {
        _host = host;
        _port = port;
        _log = log ?? NullLogger<RhpCustomClient>.Instance;
    }

    /// <inheritdoc/>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(_host, _port, ct).ConfigureAwait(false);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }

        _tcp = tcp;
        _stream = tcp.GetStream();
        _readerCts = new CancellationTokenSource();
        _reader = Task.Run(() => ReadLoopAsync(_readerCts.Token));
        _log.LogInformation("RHP connected to {Host}:{Port}", _host, _port);

        var reply = await RequestAsync(new JsonObject
        {
            ["type"] = "socket",
            ["pfam"] = "ax25",
            ["mode"] = "custom",
        }, ct).ConfigureAwait(false);

        _handle = reply.Handle ?? throw new RhpException("socketReply carried no handle.");
        _log.LogInformation("RHP ax25/custom socket opened, handle {Handle}", _handle);
    }

    /// <inheritdoc/>
    public async Task BindAsync(string localCallsign, CancellationToken ct = default)
    {
        EnsureConnected();
        await RequestAsync(new JsonObject
        {
            ["type"] = "bind",
            ["handle"] = _handle,
            ["local"] = localCallsign,
            ["port"] = null,
        }, ct).ConfigureAwait(false);

        _localCallsign = localCallsign;
        _log.LogInformation("RHP bound to {Callsign}", localCallsign);
    }

    /// <inheritdoc/>
    public async Task SendAsync(string destCallsign, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        EnsureConnected();
        if (_localCallsign is null)
            throw new RhpException("SendAsync before BindAsync — no source callsign bound.");

        // custom mode: the PID rides as data[0]; there is no separate pid wire field.
        await RequestAsync(new JsonObject
        {
            ["type"] = "sendto",
            ["handle"] = _handle,
            ["remote"] = destCallsign,
            ["local"] = _localCallsign,
            ["data"] = Latin1Codec.ToWireString(data.Span),
        }, ct).ConfigureAwait(false);
    }

    private void EnsureConnected()
    {
        if (_stream is null) throw new RhpException("Not connected — call ConnectAsync first.");
    }

    private int NextId()
    {
        // Never 0 — the wire treats a missing/zero id as "no correlation".
        while (true)
        {
            int v = Interlocked.Increment(ref _nextId);
            if (v != 0) return v;
        }
    }

    private async Task<RhpReply> RequestAsync(JsonObject request, CancellationToken ct)
    {
        int id = NextId();
        request["id"] = id;

        var tcs = new TaskCompletionSource<RhpReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, tcs))
            throw new InvalidOperationException($"Duplicate RHP request id {id}.");

        try
        {
            await SendFrameAsync(request, ct).ConfigureAwait(false);
            await using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
            var reply = await tcs.Task.ConfigureAwait(false);
            if (reply.ErrCode != 0)
                throw new RhpException($"RHP {request["type"]} failed: errCode {reply.ErrCode} '{reply.ErrText}'.");
            return reply;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task SendFrameAsync(JsonObject request, CancellationToken ct)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(request);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RhpFraming.WriteFrameAsync(_stream!, payload, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                byte[]? frame = await RhpFraming.ReadFrameAsync(_stream!, ct).ConfigureAwait(false);
                if (frame is null) break; // clean EOS
                try
                {
                    Dispatch(frame);
                }
                catch (Exception ex)
                {
                    // A malformed frame or a throwing subscriber must not kill the read loop.
                    _log.LogWarning(ex, "RHP frame dispatch failed");
                }
            }
        }
        catch (OperationCanceledException) { /* expected on dispose */ }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "RHP read loop terminated");
        }
        finally
        {
            foreach (var kvp in _pending)
                kvp.Value.TrySetException(new RhpException("RHP connection closed."));
        }
    }

    private void Dispatch(byte[] frame)
    {
        using var doc = JsonDocument.Parse(frame);
        JsonElement root = doc.RootElement;

        // A reply echoes the request id; correlate and complete the waiter.
        if (root.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out int id)
            && _pending.TryGetValue(id, out var tcs))
        {
            tcs.TrySetResult(RhpReply.From(root));
            return;
        }

        string? type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (type == "recv")
        {
            var dg = ParseRecv(root);
            DatagramReceived?.Invoke(dg);
        }
        // Other async pushes (status/close) are not needed by the datagram bridge; ignore quietly.
    }

    private static RhpDatagram ParseRecv(JsonElement root)
    {
        string source = root.TryGetProperty("remote", out var r) ? r.GetString() ?? "" : "";
        string dest = root.TryGetProperty("local", out var l) ? l.GetString() ?? "" : "";
        // custom mode: no pid field — the PID is data[0] of the payload we surface verbatim.
        string data = root.TryGetProperty("data", out var d) ? d.GetString() ?? "" : "";
        return new RhpDatagram(source, dest, Latin1Codec.FromWireString(data));
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _readerCts?.Cancel(); } catch { /* ignore */ }
        if (_reader is not null)
        {
            try { await _reader.ConfigureAwait(false); } catch { /* swallow */ }
        }
        _stream?.Dispose();
        _tcp?.Dispose();
        _readerCts?.Dispose();
        _writeLock.Dispose();
    }

    // A decoded reply frame — just the fields the datagram subset cares about.
    private readonly record struct RhpReply(int? Handle, int ErrCode, string? ErrText)
    {
        public static RhpReply From(JsonElement root)
        {
            int? handle = root.TryGetProperty("handle", out var h) && h.TryGetInt32(out int hv) ? hv : null;
            // Wire uses capital errCode/errText on every reply; accept either case defensively.
            int errCode = ReadInt(root, "errCode") ?? ReadInt(root, "errcode") ?? 0;
            string? errText = ReadString(root, "errText") ?? ReadString(root, "errtext");
            return new RhpReply(handle, errCode, errText);
        }

        private static int? ReadInt(JsonElement root, string name)
            => root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out int v) ? v : null;

        private static string? ReadString(JsonElement root, string name)
            => root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
    }
}

/// <summary>Raised on an RHP protocol or transport error (a non-zero <c>errCode</c>, or a closed link).</summary>
public sealed class RhpException : Exception
{
    /// <summary>Creates the exception with a descriptive message.</summary>
    public RhpException(string message) : base(message) { }
}
