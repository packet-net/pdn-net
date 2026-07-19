namespace Packet.Net.Rhp;

/// <summary>
/// A client of a pdn node's RHPv2 <c>ax25</c>/<c>custom</c> seam: sends AX.25 UI frames and
/// surfaces inbound ones. In <c>custom</c> mode the AX.25 PID is carried as the first payload octet
/// (<c>data[0]</c>), so this client shuttles opaque payloads and leaves the PID convention to its
/// caller. The interface keeps the bridge testable against a fake (no TCP, no node).
/// </summary>
public interface IRhpCustomClient : IAsyncDisposable
{
    /// <summary>Raised for every inbound UI datagram (an RHPv2 <c>recv</c> push).</summary>
    event Action<RhpDatagram>? DatagramReceived;

    /// <summary>
    /// Connects to the node and opens an <c>ax25</c>/<c>custom</c> socket
    /// (<c>socket{pfam:"ax25",mode:"custom"}</c>).
    /// </summary>
    Task ConnectAsync(CancellationToken ct = default);

    /// <summary>
    /// Binds the socket to a local callsign (<c>bind{handle,local:&lt;callsign&gt;,port:null}</c>).
    /// The bound callsign becomes the <c>local</c> (source) of every subsequent
    /// <see cref="SendAsync"/>.
    /// </summary>
    Task BindAsync(string localCallsign, CancellationToken ct = default);

    /// <summary>
    /// Sends an AX.25 UI frame to <paramref name="destCallsign"/>
    /// (<c>sendto{handle,remote:&lt;dest&gt;,local:&lt;bound&gt;,data}</c>). In <c>custom</c> mode
    /// <paramref name="data"/> is the whole payload with the PID as its first octet
    /// (<c>data[0]</c>); it is carried Latin-1 encoded per the wire.
    /// </summary>
    Task SendAsync(string destCallsign, ReadOnlyMemory<byte> data, CancellationToken ct = default);
}
