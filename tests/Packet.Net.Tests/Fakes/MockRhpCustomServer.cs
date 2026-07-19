using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Packet.Net.Rhp;

namespace Packet.Net.Tests.Fakes;

/// <summary>
/// A minimal RHPv2 <c>ax25</c>/<c>custom</c> server over a real loopback TCP socket, just enough to
/// exercise <see cref="RhpCustomClient"/> end-to-end: framing, id-echo replies, Latin-1 data, and
/// async <c>recv</c> pushes carrying a seqno. It answers <c>socket</c>/<c>bind</c>/<c>sendto</c> and
/// captures every <c>sendto</c> for assertions. In custom mode the PID is carried as <c>data[0]</c>,
/// so there is no separate <c>pid</c> wire field.
/// </summary>
public sealed class MockRhpCustomServer : IAsyncDisposable
{
    /// <summary>A captured sendto request (already Latin-1 decoded) — <c>Data[0]</c> is the PID.</summary>
    public readonly record struct Captured(string Remote, string Local, byte[] Data);

    private readonly TcpListener _listener;
    private readonly TaskCompletionSource<NetworkStream> _clientStream = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<Captured> _sends = new();
    private readonly object _gate = new();
    private Task? _accept;
    private int _seqno;

    private MockRhpCustomServer(TcpListener listener) => _listener = listener;

    /// <summary>The loopback port the server is listening on.</summary>
    public int Port { get; private set; }

    /// <summary>The <c>mode</c> the client asked for on its <c>socket</c> request (null until seen).</summary>
    public string? SocketMode { get; private set; }

    /// <summary>Every captured sendto, in arrival order.</summary>
    public IReadOnlyList<Captured> Sends
    {
        get { lock (_gate) return _sends.ToArray(); }
    }

    /// <summary>Starts the server on an ephemeral loopback port.</summary>
    public static MockRhpCustomServer Start()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = new MockRhpCustomServer(listener)
        {
            Port = ((IPEndPoint)listener.LocalEndpoint).Port,
        };
        server._accept = Task.Run(server.AcceptLoopAsync);
        return server;
    }

    private async Task AcceptLoopAsync()
    {
        TcpClient client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
        client.NoDelay = true;
        NetworkStream stream = client.GetStream();
        _clientStream.TrySetResult(stream);

        while (true)
        {
            byte[]? frame;
            try
            {
                frame = await RhpFraming.ReadFrameAsync(stream).ConfigureAwait(false);
            }
            catch
            {
                break;
            }
            if (frame is null) break;
            await HandleAsync(stream, frame).ConfigureAwait(false);
        }
    }

    private async Task HandleAsync(NetworkStream stream, byte[] frame)
    {
        using var doc = JsonDocument.Parse(frame);
        JsonElement root = doc.RootElement;
        string type = root.GetProperty("type").GetString()!;
        int? id = root.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out int i) ? i : null;

        if (type == "socket")
            SocketMode = root.TryGetProperty("mode", out var m) ? m.GetString() : null;

        JsonObject reply = type switch
        {
            "socket" => new JsonObject { ["type"] = "socketReply", ["handle"] = 1, ["errCode"] = 0 },
            "bind" => new JsonObject { ["type"] = "bindReply", ["handle"] = 1, ["errCode"] = 0 },
            "sendto" => CaptureSendto(root),
            _ => new JsonObject { ["type"] = type + "Reply", ["errCode"] = 2, ["errText"] = "Unsupported" },
        };
        if (id is int rid) reply["id"] = rid;

        await WriteAsync(stream, reply).ConfigureAwait(false);
    }

    private JsonObject CaptureSendto(JsonElement root)
    {
        string remote = root.GetProperty("remote").GetString()!;
        string local = root.GetProperty("local").GetString()!;
        // custom mode: no pid field — the PID rides as data[0] of the captured payload.
        string data = root.TryGetProperty("data", out var d) ? d.GetString() ?? "" : "";
        lock (_gate) _sends.Add(new Captured(remote, local, Latin1Codec.FromWireString(data)));
        return new JsonObject { ["type"] = "sendToReply", ["handle"] = 1, ["errCode"] = 0 };
    }

    /// <summary>Pushes an inbound <c>recv</c> to the connected client (source, dest, custom payload
    /// with the PID as <c>data[0]</c>).</summary>
    public async Task PushRecvAsync(string source, string dest, byte[] data)
    {
        NetworkStream stream = await _clientStream.Task.ConfigureAwait(false);
        var recv = new JsonObject
        {
            ["type"] = "recv",
            ["handle"] = 1,
            ["seqno"] = Interlocked.Increment(ref _seqno) - 1, // per-connection, starts at 0
            ["remote"] = source,
            ["local"] = dest,
            ["data"] = Latin1Codec.ToWireString(data),
        };
        await WriteAsync(stream, recv).ConfigureAwait(false);
    }

    private static async Task WriteAsync(NetworkStream stream, JsonObject obj)
        => await RhpFraming.WriteFrameAsync(stream, JsonSerializer.SerializeToUtf8Bytes(obj)).ConfigureAwait(false);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _listener.Stop();
        if (_clientStream.Task.IsCompletedSuccessfully)
            _clientStream.Task.Result.Dispose();
        if (_accept is not null)
        {
            try { await _accept.ConfigureAwait(false); } catch { /* ignore */ }
        }
    }
}
