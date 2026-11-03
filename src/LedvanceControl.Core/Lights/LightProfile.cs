using LedvanceControl.Core.Model;

namespace LedvanceControl.Core.Lights;

/// <summary>
/// Maps the standard Tuya lighting DPs of a device (v2 "dj" set: 20-26, or legacy v1 set: 1-6)
/// to high level operations: power, mode, brightness, colour temperature, colour, scene, countdown.
/// </summary>
public sealed class LightProfile
{
    public string? SwitchCode { get; private init; }
    public string? ModeCode { get; private init; }
    public DpSchema? Bright { get; private init; }
    public DpSchema? Temp { get; private init; }
    public string? ColourCode { get; private init; }
    public bool ColourV2 { get; private init; }
    public string? SceneCode { get; private init; }
    public DpSchema? Countdown { get; private init; }
    public IReadOnlyList<string> Modes { get; private init; } = [];

    public bool HasColour => ColourCode != null;
    public bool HasTemp => Temp != null;
    public bool HasBrightness => Bright != null;
    public bool HasScenes => SceneCode != null && ColourV2;

    /// <summary>Builds a profile from a device schema, or returns null when the device is not a light.</summary>
    public static LightProfile? From(IReadOnlyList<DpSchema> schema)
    {
        DpSchema? Find(params string[] codes) => codes.Select(c => schema.FirstOrDefault(d => d.Code == c)).FirstOrDefault(d => d != null);

        var sw = Find("switch_led", "led_switch", "switch_led_1");
        var bright = Find("bright_value_v2", "bright_value", "bright_value_1");
        var colour = Find("colour_data_v2", "colour_data");
        if (sw == null || (bright == null && colour == null)) return null;
        var mode = Find("work_mode");
        return new LightProfile
        {
            SwitchCode = sw.Code,
            ModeCode = mode?.Code,
            Modes = mode?.Range ?? [],
            Bright = bright,
            Temp = Find("temp_value_v2", "temp_value", "temp_value_1"),
            ColourCode = colour?.Code,
            ColourV2 = colour?.Code == "colour_data_v2",
            SceneCode = Find("scene_data_v2", "scene_data")?.Code,
            Countdown = Find("countdown_1", "countdown"),
        };
    }

    /// <summary>0-100 % to the device's brightness range.</summary>
    public long BrightnessToRaw(double percent) => Scale(Bright!, Math.Clamp(percent, 1, 100));
    public double BrightnessFromRaw(long raw) => Unscale(Bright!, raw);

    /// <summary>0 (warm) - 100 % (cold) to the device's temperature range.</summary>
    public long TempToRaw(double percent) => Scale(Temp!, Math.Clamp(percent, 0, 100));
    public double TempFromRaw(long raw) => Unscale(Temp!, raw);

    public string EncodeColour(HsvColor c) => ColourV2 ? c.ToV2Hex() : c.ToV1Hex();

    public Dictionary<string, object?> SetPower(bool on) => new() { [SwitchCode!] = on };

    public Dictionary<string, object?> SetWhite(double? brightnessPercent, double? tempPercent)
    {
        var d = new Dictionary<string, object?> { [SwitchCode!] = true };
        if (ModeCode != null) d[ModeCode] = "white";
        if (brightnessPercent is { } b && Bright != null) d[Bright.Code] = BrightnessToRaw(b);
        if (tempPercent is { } t && Temp != null) d[Temp.Code] = TempToRaw(t);
        return d;
    }

    public Dictionary<string, object?> SetColour(HsvColor c)
    {
        var d = new Dictionary<string, object?> { [SwitchCode!] = true };
        if (ModeCode != null) d[ModeCode] = "colour";
        d[ColourCode!] = EncodeColour(c);
        return d;
    }

    public Dictionary<string, object?> SetScene(string sceneData)
    {
        var d = new Dictionary<string, object?> { [SwitchCode!] = true };
        if (ModeCode != null) d[ModeCode] = "scene";
        d[SceneCode!] = sceneData;
        return d;
    }

    private static long Scale(DpSchema dp, double percent)
    {
        var raw = dp.Min + (dp.Max - dp.Min) * percent / 100.0;
        var step = Math.Max(1, dp.Step);
        return Math.Clamp((long)Math.Round(raw / step) * step, dp.Min, dp.Max);
    }

    private static double Unscale(DpSchema dp, long raw) =>
        dp.Max == dp.Min ? 100 : Math.Clamp((raw - dp.Min) * 100.0 / (dp.Max - dp.Min), 0, 100);

    /// <summary>The built-in scenes of Tuya v2 lights (scene_data_v2).</summary>
    public static readonly IReadOnlyList<(string Name, string Data)> BuiltInScenes =
    [
        ("Night", "000e0d0000000000000000c80000"),
        ("Reading", "010e0d0000000000000003e801f4"),
        ("Working", "020e0d0000000000000003e803e8"),
        ("Leisure", "030e0d0000000000000001f401f4"),
        ("Soft", "04464602007803e803e800000000464602007803e8000a00000000"),
        ("Rainbow", "05464601000003e803e800000000464601007803e803e80000000046460100f003e803e800000000"),
        ("Shine", "06464601000003e803e800000000464601007803e803e80000000046460100f003e803e800000000"),
        ("Gorgeous", "07464602000003e803e800000000464602007803e803e80000000046460200f003e803e800000000464602003d03e803e80000000046460200ae03e803e800000000464602011303e803e800000000"),
    ];
}
