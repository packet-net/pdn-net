using Packet.Net.Rhp;

namespace Packet.Net.Tests.Fakes;

/// <summary>
/// An in-memory <see cref="IRhpDgramClient"/> for bridge tests: captures every
/// <see cref="SendUiAsync"/> and lets the test inject inbound datagrams via
/// <see cref="InjectInbound"/> as if the node pushed a <c>recv</c>.
/// </summary>
public sealed class FakeRhpDgramClient : IRhpDgramClient
{
    /// <summary>A captured outbound sendto.</summary>
    public readonly record struct Sent(string DestCallsign, int Pid, byte[] Data);

    /// <summary>Every outbound sendto the bridge issued, in order.</summary>
    public List<Sent> Sends { get; } = new();

    /// <inheritdoc/>
    public event Action<RhpDatagram>? DatagramReceived;

    /// <inheritdoc/>
    public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task BindAsync(string localCallsign, CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task SendUiAsync(string destCallsign, int pid, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        Sends.Add(new Sent(destCallsign, pid, data.ToArray()));
        return Task.CompletedTask;
    }

    /// <summary>Raises <see cref="DatagramReceived"/> as if the node pushed an inbound <c>recv</c>.</summary>
    public void InjectInbound(RhpDatagram dg) => DatagramReceived?.Invoke(dg);

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
