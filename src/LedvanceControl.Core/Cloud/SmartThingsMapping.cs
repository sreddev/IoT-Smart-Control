using System.Globalization;
using System.Text.Json.Nodes;
using LedvanceControl.Core.Lights;
using LedvanceControl.Core.Model;

namespace LedvanceControl.Core.Cloud;

/// <summary>
/// Bridges SmartThings capabilities and the app's internal DP-code model, so SmartThings devices
/// drive the same light UI as Tuya devices.
///
/// SmartThings scales differ from Tuya: hue and saturation are 0-100 (hue is a percentage of the
/// 360° wheel, so green = 120/3.6 ≈ 33), level is 0-100, and colour temperature is in Kelvin.
/// Internally the app keeps Tuya conventions (hue 0-360, sat/val 0-1000, colour as v2 hex), so this
/// class converts in both directions. That is why sending "green" works: 120° becomes hue 33 here.
/// </summary>
public static class SmartThingsMapping
{
    public const int MinKelvin = 2700;
    public const int MaxKelvin = 6500;

    public const string CapSwitch = "switch";
    public const string CapLevel = "switchLevel";
    public const string CapColor = "colorControl";
    public const string CapColorTemp = "colorTemperature";

    /// <summary>Builds a DP schema (so <see cref="LightProfile"/> and the UI work) from the device's capabilities.</summary>
    public static List<DpSchema> BuildSchema(IReadOnlyList<string> capabilities)
    {
        var schema = new List<DpSchema>();
        bool hasColor = capabilities.Contains(CapColor);
        bool hasTemp = capabilities.Contains(CapColorTemp);
        bool hasLevel = capabilities.Contains(CapLevel);

        if (capabilities.Contains(CapSwitch))
            schema.Add(new DpSchema { Id = "switch", Code = "switch_led", Name = "Power", Type = DpType.Bool });
        if (hasColor || hasTemp)
            schema.Add(new DpSchema { Id = "work_mode", Code = "work_mode", Name = "Mode", Type = DpType.Enum, Range = Modes(hasColor, hasTemp) });
        if (hasLevel)
            schema.Add(new DpSchema { Id = "level", Code = "bright_value_v2", Name = "Brightness", Type = DpType.Value, Min = 0, Max = 100, Unit = "%" });
        if (hasTemp)
            schema.Add(new DpSchema { Id = "colorTemperature", Code = "temp_value_v2", Name = "Colour temperature", Type = DpType.Value, Min = 0, Max = 100, Unit = "%" });
        if (hasColor)
            schema.Add(new DpSchema { Id = "color", Code = "colour_data_v2", Name = "Colour", Type = DpType.String });
        return schema;
    }

    private static List<string> Modes(bool color, bool temp)
    {
        var m = new List<string>();
        if (temp) m.Add("white");
        if (color) m.Add("colour");
        return m;
    }

    public static bool IsLight(IReadOnlyList<string> caps) =>
        caps.Contains(CapSwitch) && (caps.Contains(CapLevel) || caps.Contains(CapColor) || caps.Contains(CapColorTemp));

    public static string GuessCategory(IReadOnlyList<string> caps) => IsLight(caps) ? "dj" : "cz";

    // ---------------------------------------------------------------- status → internal codes

    /// <summary>Converts a SmartThings status object (main component) into internal DP-code values.</summary>
    public static Dictionary<string, object?> StatusToCodes(JsonObject main)
    {
        var values = new Dictionary<string, object?>();
        string? Attr(string cap, string attr) => main[cap]?[attr]?["value"]?.ToString();
        // JSON numbers are always invariant (dot decimal), regardless of the machine's locale.
        double Num(string cap, string attr) =>
            double.TryParse(Attr(cap, attr), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0;

        if (Attr(CapSwitch, "switch") is { } sw) values["switch_led"] = sw == "on";

        int level = 100;
        if (main[CapLevel]?["level"]?["value"] != null)
        {
            level = (int)Math.Round(Num(CapLevel, "level"));
            values["bright_value_v2"] = (long)level;
        }
        if (main[CapColorTemp]?["colorTemperature"]?["value"] != null)
            values["temp_value_v2"] = (long)Math.Round(KelvinToPercent(Num(CapColorTemp, "colorTemperature")));

        bool hasColor = main.ContainsKey(CapColor);
        if (hasColor)
        {
            var hue = Num(CapColor, "hue");
            var sat = Num(CapColor, "saturation");
            var c = new HsvColor((int)Math.Round(hue * 3.6), (int)Math.Round(sat * 10), Math.Max(1, level * 10));
            values["colour_data_v2"] = c.ToV2Hex();
            if (main.ContainsKey(CapColorTemp) || main.ContainsKey(CapLevel))
                values["work_mode"] = sat > 0 ? "colour" : "white";
        }
        return values;
    }

    // ---------------------------------------------------------------- internal codes → commands

    /// <summary>Converts internal DP-code writes into SmartThings commands for one device.</summary>
    public static JsonArray CodesToCommands(IReadOnlyDictionary<string, object?> byCode)
    {
        var cmds = new JsonArray();
        void Add(string cap, string command, params JsonNode?[] args)
        {
            var arr = new JsonArray();
            foreach (var a in args) arr.Add(a);
            cmds.Add(new JsonObject { ["component"] = "main", ["capability"] = cap, ["command"] = command, ["arguments"] = arr });
        }

        foreach (var (code, value) in byCode)
        {
            switch (code)
            {
                case "switch_led" or "switch_1" or "switch":
                    Add(CapSwitch, ToBool(value) ? "on" : "off");
                    break;
                case "work_mode":
                    break; // implied by which colour/temperature command is sent
                case "bright_value_v2" or "bright_value" or "bright_value_1":
                    Add(CapLevel, "setLevel", (int)Math.Clamp(ToDouble(value), 1, 100));
                    break;
                case "temp_value_v2" or "temp_value" or "temp_value_1":
                    Add(CapColorTemp, "setColorTemperature", (int)Math.Round(PercentToKelvin(ToDouble(value))));
                    break;
                case "colour_data_v2" or "colour_data":
                    if (HsvColor.TryParse(value?.ToString(), out var c))
                    {
                        Add(CapColor, "setColor", new JsonObject
                        {
                            ["hue"] = Math.Round(c.H / 3.6, 1),
                            ["saturation"] = Math.Round(c.S / 10.0, 1),
                        });
                        if (c.V > 0) Add(CapLevel, "setLevel", (int)Math.Clamp(c.V / 10, 1, 100));
                    }
                    break;
            }
        }
        return cmds;
    }

    public static double KelvinToPercent(double kelvin) =>
        Math.Clamp((kelvin - MinKelvin) * 100.0 / (MaxKelvin - MinKelvin), 0, 100);

    public static double PercentToKelvin(double percent) =>
        Math.Clamp(MinKelvin + percent / 100.0 * (MaxKelvin - MinKelvin), MinKelvin, MaxKelvin);

    private static bool ToBool(object? v) => v switch { bool b => b, string s => s is "on" or "true" or "1" or "True", long l => l != 0, _ => false };
    private static double ToDouble(object? v) => v switch
    {
        double d => d,
        long l => l,
        int i => i,
        bool b => b ? 100 : 0,
        string s when double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var r) => r,
        _ => 0,
    };
}
