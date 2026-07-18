namespace Packet.Net.Rhp;

/// <summary>
/// A client of a pdn node's RHPv2 <c>ax25</c>/<c>dgram</c> seam: sends AX.25 UI frames and
/// surfaces inbound ones. The interface keeps the bridge testable against a fake (no TCP, no node).
/// </summary>
public interface IRhpDgramClient : IAsyncDisposable
{
    /// <summary>Raised for every inbound UI datagram (an RHPv2 <c>recv</c> push).</summary>
    event Action<RhpDatagram>? DatagramReceived;

    /// <summary>
    /// Connects to the node and opens an <c>ax25</c>/<c>dgram</c> socket
    /// (<c>socket{pfam:"ax25",mode:"dgram"}</c>).
    /// </summary>
    Task ConnectAsync(CancellationToken ct = default);

    /// <summary>
    /// Binds the socket to a local callsign (<c>bind{handle,local:&lt;callsign&gt;,port:null}</c>).
    /// The bound callsign becomes the <c>local</c> (source) of every subsequent
    /// <see cref="SendUiAsync"/>.
    /// </summary>
    Task BindAsync(string localCallsign, CancellationToken ct = default);

    /// <summary>
    /// Sends an AX.25 UI frame to <paramref name="destCallsign"/>
    /// (<c>sendto{handle,remote:&lt;dest&gt;,local:&lt;bound&gt;,pid,data}</c>). The data is carried
    /// Latin-1 encoded per the wire.
    /// </summary>
    Task SendUiAsync(string destCallsign, int pid, ReadOnlyMemory<byte> data, CancellationToken ct = default);
}
