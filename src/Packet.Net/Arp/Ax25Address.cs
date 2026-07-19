namespace Packet.Net.Arp;

/// <summary>
/// Encodes and decodes AX.25 callsigns in the 7-byte shifted format used in ARP hardware addresses
/// and AX.25 frame headers. Each of the first 6 bytes is the ASCII character shifted left by 1
/// (space-padded); byte 6 carries the SSID in bits 1–4 (also shifted left by 1).
/// </summary>
public static class Ax25Address
{
    public const int Length = 7;

    /// <summary>Encodes "CALL" or "CALL-SSID" to 7 shifted bytes.</summary>
    public static byte[] Encode(string callsign)
    {
        var buf = new byte[Length];
        string baseCall = callsign;
        int ssid = 0;

        int dash = callsign.IndexOf('-');
        if (dash >= 0)
        {
            baseCall = callsign[..dash];
            int.TryParse(callsign[(dash + 1)..], out ssid);
            ssid &= 0x0F;
        }

        for (int i = 0; i < 6; i++)
        {
            byte c = i < baseCall.Length ? (byte)char.ToUpperInvariant(baseCall[i]) : (byte)' ';
            buf[i] = (byte)(c << 1);
        }

        buf[6] = (byte)((ssid << 1) & 0x1E);
        return buf;
    }

    /// <summary>Decodes 7 shifted bytes to "CALL" or "CALL-SSID".</summary>
    public static string Decode(ReadOnlySpan<byte> addr)
    {
        if (addr.Length < Length) return "";

        Span<char> chars = stackalloc char[6];
        int len = 0;
        for (int i = 0; i < 6; i++)
        {
            char c = (char)((addr[i] >> 1) & 0x7F);
            if (c != ' ') chars[len++] = c;
        }

        int ssid = (addr[6] >> 1) & 0x0F;
        string call = new(chars[..len]);
        return ssid != 0 ? $"{call}-{ssid}" : call;
    }
}
