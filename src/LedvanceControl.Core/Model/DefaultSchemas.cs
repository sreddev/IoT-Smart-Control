using System.Text.Json;

namespace LedvanceControl.Core.Model;

/// <summary>
/// Standard Tuya DP layouts used when no cloud schema is available (e.g. devices imported with only id/key/ip).
/// </summary>
public static class DefaultSchemas
{
    private static readonly string[] LightCategories = ["dj", "dd", "fwd", "dc", "xdd", "gyd", "fsd", "tyndj", "tgq", "sxd", "light"];
    private static readonly string[] PlugCategories = ["cz", "pc", "kg", "tdq", "plug", "switch"];

    public static List<DpSchema> LightV2() =>
    [
        new() { Id = "20", Code = "switch_led", Name = "Power", Type = DpType.Bool },
        new() { Id = "21", Code = "work_mode", Name = "Mode", Type = DpType.Enum, Range = ["white", "colour", "scene", "music"] },
        new() { Id = "22", Code = "bright_value_v2", Name = "Brightness", Type = DpType.Value, Min = 10, Max = 1000 },
        new() { Id = "23", Code = "temp_value_v2", Name = "Colour temperature", Type = DpType.Value, Min = 0, Max = 1000 },
        new() { Id = "24", Code = "colour_data_v2", Name = "Colour", Type = DpType.String },
        new() { Id = "25", Code = "scene_data_v2", Name = "Scene", Type = DpType.String },
        new() { Id = "26", Code = "countdown_1", Name = "Countdown", Type = DpType.Value, Min = 0, Max = 86400, Unit = "s" },
    ];

    public static List<DpSchema> LightV1() =>
    [
        new() { Id = "1", Code = "switch_led", Name = "Power", Type = DpType.Bool },
        new() { Id = "2", Code = "work_mode", Name = "Mode", Type = DpType.Enum, Range = ["white", "colour", "scene", "scene_1", "scene_2", "scene_3", "scene_4"] },
        new() { Id = "3", Code = "bright_value", Name = "Brightness", Type = DpType.Value, Min = 25, Max = 255 },
        new() { Id = "4", Code = "temp_value", Name = "Colour temperature", Type = DpType.Value, Min = 0, Max = 255 },
        new() { Id = "5", Code = "colour_data", Name = "Colour", Type = DpType.String },
    ];

    public static List<DpSchema> Plug() =>
    [
        new() { Id = "1", Code = "switch_1", Name = "Power", Type = DpType.Bool },
        new() { Id = "9", Code = "countdown_1", Name = "Countdown", Type = DpType.Value, Min = 0, Max = 86400, Unit = "s" },
        new() { Id = "17", Code = "add_ele", Name = "Energy added", Type = DpType.Value, Mode = "ro", Max = 50000, Scale = 3, Unit = "kWh" },
        new() { Id = "18", Code = "cur_current", Name = "Current", Type = DpType.Value, Mode = "ro", Max = 30000, Unit = "mA" },
        new() { Id = "19", Code = "cur_power", Name = "Power draw", Type = DpType.Value, Mode = "ro", Max = 80000, Scale = 1, Unit = "W" },
        new() { Id = "20", Code = "cur_voltage", Name = "Voltage", Type = DpType.Value, Mode = "ro", Max = 5000, Scale = 1, Unit = "V" },
    ];

    /// <summary>Picks a schema from the category and/or the DP ids the device reported.</summary>
    public static List<DpSchema> Guess(string? category, IReadOnlyDictionary<string, JsonElement>? observed)
    {
        bool Has(string id) => observed?.ContainsKey(id) == true;
        bool light = category != null && LightCategories.Contains(category);
        bool plug = category != null && PlugCategories.Contains(category);

        List<DpSchema> schema;
        if (light || (category == null && Has("20") && (Has("22") || Has("24"))))
            schema = observed == null || Has("20") ? LightV2() : LightV1();
        else if (plug || (category == null && Has("1") && Has("19")))
            schema = Plug();
        else if (category == null && Has("1") && Has("2") && Has("3") && observed!.TryGetValue("2", out var m) && m.ValueKind == JsonValueKind.String)
            schema = LightV1();
        else
            schema = [];

        if (observed != null)
        {
            // keep only DPs the device actually has, and add unknown ones generically
            if (observed.Count > 0) schema = schema.Where(s => Has(s.Id)).ToList();
            foreach (var (id, value) in observed)
            {
                if (schema.Any(s => s.Id == id)) continue;
                schema.Add(new DpSchema
                {
                    Id = id,
                    Code = id,
                    Name = $"DP {id}",
                    Type = value.ValueKind switch
                    {
                        JsonValueKind.True or JsonValueKind.False => DpType.Bool,
                        JsonValueKind.Number => DpType.Value,
                        _ => DpType.String,
                    },
                    Max = value.ValueKind == JsonValueKind.Number ? Math.Max(1000, value.GetInt64()) : 100,
                });
            }
        }
        return schema.OrderBy(s => int.TryParse(s.Id, out var n) ? n : int.MaxValue).ToList();
    }
}
