using System.Text;

namespace Packet.Net.Rhp;

/// <summary>
/// Moves binary payloads through the JSON <c>data</c> field. RHPv2 carries bytes as a
/// <b>Latin-1 (ISO-8859-1) string — one byte per code unit — not base64</b>. Every byte 0x00..0xFF
/// maps to the code point of the same value; the JSON serialiser then escapes control characters.
/// This is the single most important wire nuance to get right when talking to a pdn/XRouter node.
/// </summary>
public static class Latin1Codec
{
    /// <summary>Encodes a byte payload as the Latin-1 wire string.</summary>
    public static string ToWireString(ReadOnlySpan<byte> bytes)
        => bytes.IsEmpty ? string.Empty : Encoding.Latin1.GetString(bytes);

    /// <summary>Decodes a Latin-1 wire string back into the original bytes.</summary>
    public static byte[] FromWireString(string s)
        => string.IsNullOrEmpty(s) ? Array.Empty<byte>() : Encoding.Latin1.GetBytes(s);
}
