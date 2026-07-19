namespace Packet.Net.Rhp;

/// <summary>
/// An inbound AX.25 UI datagram delivered by the pdn node (an RHPv2 <c>recv</c> push on a
/// <c>custom</c>-mode socket).
/// </summary>
/// <param name="Source">The transmitting station's callsign (the frame's <c>remote</c> field).</param>
/// <param name="Dest">The destination callsign the frame was addressed to (the <c>local</c> field).</param>
/// <param name="Data">
/// The custom-mode payload: <c>Data[0]</c> is the AX.25 protocol id (0xCC = IP, 0xCD = ARP) and
/// <c>Data[1..]</c> is the UI frame's information field. In RHPv2 <c>custom</c> mode the PID is the
/// first payload octet rather than a separate wire field.
/// </param>
public readonly record struct RhpDatagram(string Source, string Dest, ReadOnlyMemory<byte> Data);
