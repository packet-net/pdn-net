using Packet.Net.Rhp;

namespace Packet.Net.Tests.Fakes;

/// <summary>
/// An in-memory <see cref="IRhpCustomClient"/> for bridge tests: captures every
/// <see cref="SendAsync"/> and lets the test inject inbound datagrams via
/// <see cref="InjectInbound"/> as if the node pushed a <c>recv</c>.
/// </summary>
public sealed class FakeRhpCustomClient : IRhpCustomClient
{
    /// <summary>A captured outbound sendto — <c>Data[0]</c> is the PID (custom mode).</summary>
    public readonly record struct Sent(string DestCallsign, byte[] Data);

    /// <summary>Every outbound sendto the bridge issued, in order.</summary>
    public List<Sent> Sends { get; } = new();

    /// <inheritdoc/>
    public event Action<RhpDatagram>? DatagramReceived;

    /// <inheritdoc/>
    public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task BindAsync(string localCallsign, CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task SendAsync(string destCallsign, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        Sends.Add(new Sent(destCallsign, data.ToArray()));
        return Task.CompletedTask;
    }

    /// <summary>Raises <see cref="DatagramReceived"/> as if the node pushed an inbound <c>recv</c>.</summary>
    public void InjectInbound(RhpDatagram dg) => DatagramReceived?.Invoke(dg);

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
