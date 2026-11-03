using System.Text.Json.Nodes;

namespace LedvanceControl.Core.Model;

/// <summary>
/// Imports a "devices.json" file as produced by the tinytuya wizard (python -m tinytuya wizard),
/// which contains ids, local keys and DP mappings.
/// </summary>
public static class TinyTuyaImporter
{
    public static List<DeviceConfig> Import(string json)
    {
        var root = JsonNode.Parse(json) as JsonArray ?? throw new FormatException("Expected a JSON array of devices.");
        var list = new List<DeviceConfig>();
        foreach (var n in root)
        {
            var id = n?["id"]?.ToString();
            if (string.IsNullOrEmpty(id)) continue;
            var dev = new DeviceConfig
            {
                Id = id,
                Name = n!["name"]?.ToString() ?? id,
                LocalKey = n["key"]?.ToString() is { Length: > 0 } k ? k : n["local_key"]?.ToString(),
                Ip = n["ip"]?.ToString() is { Length: > 0 } ip ? ip : null,
                Version = n["version"]?.ToString() is { Length: > 0 } v ? v : "3.3",
                Category = n["category"]?.ToString(),
                ProductId = n["product_id"]?.ToString(),
                ProductName = n["product_name"]?.ToString(),
                Icon = n["icon"]?.ToString(),
            };
            if (n["mapping"] is JsonObject mapping)
            {
                foreach (var (dpId, m) in mapping)
                {
                    if (m == null) continue;
                    var values = m["values"] as JsonObject;
                    var dp = new DpSchema
                    {
                        Id = dpId,
                        Code = m["code"]?.ToString() ?? dpId,
                        Type = (m["type"]?.ToString() ?? "").ToLowerInvariant() switch
                        {
                            "boolean" or "bool" => DpType.Bool,
                            "integer" or "value" => DpType.Value,
                            "enum" => DpType.Enum,
                            "string" => DpType.String,
                            "json" => DpType.Json,
                            "bitmap" => DpType.Bitmap,
                            _ => DpType.Raw,
                        },
                    };
                    if (values != null)
                    {
                        if (long.TryParse(values["min"]?.ToString(), out var min)) dp.Min = min;
                        if (long.TryParse(values["max"]?.ToString(), out var max)) dp.Max = max;
                        if (long.TryParse(values["step"]?.ToString(), out var step)) dp.Step = Math.Max(1, step);
                        if (int.TryParse(values["scale"]?.ToString(), out var scale)) dp.Scale = scale;
                        dp.Unit = values["unit"]?.ToString();
                        if (values["range"] is JsonArray range) dp.Range = range.Select(r => r?.ToString() ?? "").ToList();
                        if (values["label"] is JsonArray label) dp.Range = label.Select(r => r?.ToString() ?? "").ToList();
                    }
                    dev.Schema.Add(dp);
                }
                dev.Schema = dev.Schema.OrderBy(s => int.TryParse(s.Id, out var i) ? i : int.MaxValue).ToList();
            }
            list.Add(dev);
        }
        return list;
    }
}
