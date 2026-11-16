using System.Globalization;
using System.Text.Json.Nodes;
using LedvanceControl.Core.Lights;
using LedvanceControl.Core.Model;

namespace LedvanceControl.Core.Local;

/// <summary>
/// Bridges Matter lights (On/Off, Level Control and Color Control clusters, as normalised by the
/// bridge) and the app's internal DP-code model, so Matter bulbs drive the same light UI, groups,
/// scenes and schedules as Tuya and SmartThings devices.
///
/// Internal conventions kept here:
///   bright_value_v2  1-100 (%)
///   temp_value_v2    Kelvin, with the schema range set to the bulb's physical min/max — so
///                    <see cref="LightProfile.TempToRaw"/> (0 % = warm) yields Kelvin directly
///   colour_data_v2   Tuya v2 hex (hue 0-360, sat/val 0-1000), val mirroring the brightness
/// </summary>
public static class MatterMapping
{
    public const string Switch = "switch_led";
    public const string Mode = "work_mode";
    public const string Bright = "bright_value_v2";
    public const string Temp = "temp_value_v2";
    public const string Colour = "colour_data_v2";

    /// <summary>Builds a DP schema (so <see cref="LightProfile"/> and the UI work) from the device's capabilities.</summary>
    public static List<DpSchema> BuildSchema(MatterCapabilities caps)
    {
        var schema = new List<DpSchema>
        {
            new() { Id = "onOff", Code = Switch, Name = "Power", Type = DpType.Bool },
        };
        if (caps.HasColour && caps.Ct)
            schema.Add(new DpSchema { Id = "colorMode", Code = Mode, Name = "Mode", Type = DpType.Enum, Range = ["white", "colour"] });
        if (caps.Level)
            schema.Add(new DpSchema { Id = "currentLevel", Code = Bright, Name = "Brightness", Type = DpType.Value, Min = 0, Max = 100, Unit = "%" }); // raw == percent (LightProfile clamps to >= 1 %)
        if (caps.Ct)
            schema.Add(new DpSchema
            {
                Id = "colorTemperature", Code = Temp, Name = "Colour temperature", Type = DpType.Value,
                Min = caps.CtMinKelvin, Max = caps.CtMaxKelvin, Step = 50, Unit = "K",
            });
        if (caps.HasColour)
            schema.Add(new DpSchema { Id = "color", Code = Colour, Name = "Colour", Type = DpType.String });
        return schema;
    }

    /// <summary>"dj" (light) when it dims or has colour, otherwise "cz" (plug / on-off device).</summary>
    public static string GuessCategory(MatterCapabilities caps) => caps.Level || caps.HasColour || caps.Ct ? "dj" : "cz";

    // ---------------------------------------------------------------- state → internal codes

    public static Dictionary<string, object?> StateToCodes(MatterLightState s, IReadOnlyList<DpSchema> schema)
    {
        bool Has(string code) => schema.Any(d => d.Code == code);
        var values = new Dictionary<string, object?>();
        if (s.On is { } on) values[Switch] = on;
        if (s.Level is { } level && Has(Bright)) values[Bright] = (long)Math.Clamp(level, 1, 100);
        if (s.ColorTempKelvin is { } k && schema.FirstOrDefault(d => d.Code == Temp) is { } t)
            values[Temp] = Math.Clamp((long)k, t.Min, t.Max);
        if (s.Hue is { } h && s.Saturation is { } sat && Has(Colour))
        {
            var v = Math.Max(10, (s.Level ?? 100) * 10);
            values[Colour] = new HsvColor(Math.Clamp(h, 0, 360), Math.Clamp(sat * 10, 0, 1000), v).ToV2Hex();
        }
        if (Has(Mode) && s.ColorMode != null)
            values[Mode] = s.ColorMode == "ct" ? "white" : "colour";
        return values;
    }

    // ---------------------------------------------------------------- internal codes → command

    /// <summary>
    /// Converts internal DP-code writes into one bridge "set" command:
    /// <c>{ on, level, hue, saturation, colorTempKelvin }</c> (only the fields that are needed).
    /// When both a colour and a colour temperature are present (e.g. a captured scene), work_mode decides.
    /// </summary>
    public static JsonObject CodesToCommand(IReadOnlyDictionary<string, object?> byCode)
    {
        var cmd = new JsonObject();
        string? mode = byCode.TryGetValue(Mode, out var m) ? m?.ToString() : null;
        HsvColor? colour = null;
        double? kelvin = null;

        foreach (var (code, value) in byCode)
        {
            switch (code)
            {
                case Switch or "switch_1" or "switch" or "led_switch":
                    cmd["on"] = ToBool(value);
                    break;
                case Bright or "bright_value":
                    cmd["level"] = (int)Math.Clamp(Math.Round(ToDouble(value)), 1, 100);
                    break;
                case Temp or "temp_value":
                    kelvin = ToDouble(value);
                    break;
                case Colour or "colour_data":
                    if (HsvColor.TryParse(value?.ToString(), out var c)) colour = c;
                    break;
            }
        }

        if (colour is { } col && mode != "white")
        {
            cmd["hue"] = Math.Clamp(col.H, 0, 360);
            cmd["saturation"] = Math.Round(Math.Clamp(col.S, 0, 1000) / 10.0, 1);
            // the colour's value channel carries the brightness in colour mode (unless set explicitly)
            if (col.V > 0 && !cmd.ContainsKey("level")) cmd["level"] = (int)Math.Clamp(Math.Round(col.V / 10.0), 1, 100);
        }
        else if (kelvin is { } k && mode != "colour")
        {
            cmd["colorTempKelvin"] = (int)Math.Round(k);
        }

        // Anything that changes the light implies "on" (as the Tuya profile does), unless explicitly off.
        if (!cmd.ContainsKey("on") && (cmd.ContainsKey("level") || cmd.ContainsKey("hue") || cmd.ContainsKey("colorTempKelvin")))
            cmd["on"] = true;
        return cmd;
    }

    private static bool ToBool(object? v) => v switch
    {
        bool b => b,
        string s => s is "on" or "true" or "1" or "True",
        long l => l != 0,
        int i => i != 0,
        double d => d != 0,
        _ => false,
    };

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
