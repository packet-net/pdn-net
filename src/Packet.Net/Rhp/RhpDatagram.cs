namespace Packet.Net.Rhp;

/// <summary>
/// An inbound AX.25 UI datagram delivered by the pdn node (an RHPv2 <c>recv</c> push on a
/// <c>dgram</c> socket).
/// </summary>
/// <param name="Source">The transmitting station's callsign (the frame's <c>remote</c> field).</param>
/// <param name="Dest">The destination callsign the frame was addressed to (the <c>local</c> field).</param>
/// <param name="Pid">The AX.25 protocol id byte (0xCC = IP, 0xCD = ARP).</param>
/// <param name="Data">The UI frame's information field.</param>
public readonly record struct RhpDatagram(string Source, string Dest, int Pid, ReadOnlyMemory<byte> Data);
