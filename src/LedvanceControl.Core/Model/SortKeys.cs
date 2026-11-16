using System.Net;

namespace LedvanceControl.Core.Model;

/// <summary>Values that sort naturally in UI tables (text sorting puts "192.168.1.100" before "192.168.1.20").</summary>
public static class SortKeys
{
    /// <summary>
    /// A sort key for an IP address (optionally with ":port"): IPv4 sorts numerically octet by octet and before
    /// IPv6; anything unparsable goes last, as text.
    /// </summary>
    public static string Ip(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "~";
        var s = text.Trim();
        var colon = s.LastIndexOf(':');
        if (colon > 0 && s.Count(c => c == ':') == 1) s = s[..colon]; // "a.b.c.d:port"
        if (!IPAddress.TryParse(s, out var ip)) return "~" + text;
        var bytes = ip.GetAddressBytes();
        return (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "4:" : "6:") + Convert.ToHexString(bytes);
    }
}
