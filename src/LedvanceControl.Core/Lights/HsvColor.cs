using System.Globalization;
using System.Text.Json;

namespace LedvanceControl.Core.Lights;

/// <summary>
/// Colour in Tuya terms: hue 0-360, saturation and value 0-1000.
/// Encodes to the v2 DP format "HHHHSSSSVVVV" (hex) or the legacy v1 format "rrggbb0hhhssvv".
/// </summary>
public readonly record struct HsvColor(int H, int S, int V)
{
    public string ToV2Hex() => $"{Math.Clamp(H, 0, 360):x4}{Math.Clamp(S, 0, 1000):x4}{Math.Clamp(V, 0, 1000):x4}";

    public string ToV1Hex()
    {
        var (r, g, b) = ToRgb();
        int s = (int)Math.Round(S * 255 / 1000.0), v = (int)Math.Round(V * 255 / 1000.0);
        return $"{r:x2}{g:x2}{b:x2}0{Math.Clamp(H, 0, 360):x3}{s:x2}{v:x2}";
    }

    /// <summary>JSON used by the cloud standard instruction set (v2: 0-1000, v1: 0-255).</summary>
    public string ToCloudJson(bool v2) => v2
        ? JsonSerializer.Serialize(new { h = H, s = S, v = V })
        : JsonSerializer.Serialize(new { h = H, s = S * 255 / 1000, v = V * 255 / 1000 });

    public (byte R, byte G, byte B) ToRgb()
    {
        double h = (H % 360 + 360) % 360 / 60.0, s = S / 1000.0, v = V / 1000.0;
        double c = v * s, x = c * (1 - Math.Abs(h % 2 - 1)), m = v - c;
        (double r, double g, double b) = (int)h switch
        {
            0 => (c, x, 0d),
            1 => (x, c, 0d),
            2 => (0d, c, x),
            3 => (0d, x, c),
            4 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return ((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    public static HsvColor FromRgb(byte r, byte g, byte b)
    {
        double rd = r / 255.0, gd = g / 255.0, bd = b / 255.0;
        double max = Math.Max(rd, Math.Max(gd, bd)), min = Math.Min(rd, Math.Min(gd, bd)), d = max - min;
        double h = d == 0 ? 0 : max == rd ? 60 * (((gd - bd) / d) % 6) : max == gd ? 60 * ((bd - rd) / d + 2) : 60 * ((rd - gd) / d + 4);
        if (h < 0) h += 360;
        return new HsvColor((int)Math.Round(h), (int)Math.Round(max == 0 ? 0 : d / max * 1000), (int)Math.Round(max * 1000));
    }

    /// <summary>Parses v2 hex (12 chars), v1 hex (14 chars) or cloud JSON ({"h":..,"s":..,"v":..}).</summary>
    public static bool TryParse(string? text, out HsvColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        try
        {
            if (text.StartsWith('{'))
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                color = new HsvColor(root.GetProperty("h").GetInt32(), root.GetProperty("s").GetInt32(), root.GetProperty("v").GetInt32());
                return true;
            }
            if (text.Length == 12)
            {
                color = new HsvColor(Hex(text, 0, 4), Hex(text, 4, 4), Hex(text, 8, 4));
                return true;
            }
            if (text.Length == 14)
            {
                color = new HsvColor(Hex(text, 7, 3), Hex(text, 10, 2) * 1000 / 255, Hex(text, 12, 2) * 1000 / 255);
                return true;
            }
        }
        catch (Exception ex) when (ex is FormatException or JsonException or KeyNotFoundException or InvalidOperationException) { }
        return false;
    }

    private static int Hex(string s, int start, int len) => int.Parse(s.AsSpan(start, len), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}
