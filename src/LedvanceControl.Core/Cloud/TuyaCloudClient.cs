using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LedvanceControl.Core.Model;

namespace LedvanceControl.Core.Cloud;

public sealed class TuyaCloudException(string message, int? code = null) : Exception(message)
{
    public int? Code { get; } = code;
}

public sealed record CloudDevice(
    string Id, string Name, string? LocalKey, string? Category, string? ProductId, string? ProductName,
    string? Uid, string? HomeId, bool Online, string? Icon, Dictionary<string, JsonElement> Status);

public sealed record CloudHome(string HomeId, string Name);
public sealed record CloudRoom(string RoomId, string Name, IReadOnlyList<string> DeviceIds);
public sealed record CloudScene(string SceneId, string Name, string HomeId, bool Enabled);
public sealed record CloudAutomation(string AutomationId, string Name, string HomeId, bool Enabled);

/// <summary>
/// Client for the official Tuya IoT Platform OpenAPI (https://developer.tuya.com).
/// Uses the "simple mode" token flow with a cloud project's Access ID/Secret, and reaches the
/// devices of every app account linked to that project.
/// </summary>
public sealed class TuyaCloudClient : IDisposable
{
    private static readonly Dictionary<string, string> RegionHosts = new()
    {
        ["eu"] = "https://openapi.tuyaeu.com",
        ["weu"] = "https://openapi-weaz.tuyaeu.com",
        ["us"] = "https://openapi.tuyaus.com",
        ["ueaz"] = "https://openapi-ueaz.tuyaus.com",
        ["cn"] = "https://openapi.tuyacn.com",
        ["in"] = "https://openapi.tuyain.com",
    };

    public static IReadOnlyCollection<string> Regions => RegionHosts.Keys;

    private readonly HttpClient _http;
    private readonly string _clientId;
    private readonly string _secret;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _tokenExpiry;

    public TuyaCloudClient(string region, string clientId, string clientSecret, HttpMessageHandler? handler = null)
    {
        if (!RegionHosts.TryGetValue(region, out var host)) throw new ArgumentException($"Unknown region '{region}'");
        _clientId = clientId;
        _secret = clientSecret;
        _http = handler == null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri(host);
        _http.Timeout = TimeSpan.FromSeconds(15);
    }

    public void Dispose() => _http.Dispose();

    // ---------------------------------------------------------------- devices

    /// <summary>All devices of every app account linked to the cloud project (includes local keys).</summary>
    public async Task<List<CloudDevice>> GetDevicesAsync(CancellationToken ct = default)
    {
        var list = new List<CloudDevice>();
        string? lastRowKey = null;
        do
        {
            var path = "/v1.0/iot-01/associated-users/devices?size=100" + (lastRowKey != null ? "&last_row_key=" + Uri.EscapeDataString(lastRowKey) : "");
            var result = await RequestAsync(HttpMethod.Get, path, null, ct);
            foreach (var d in result["devices"]?.AsArray() ?? [])
                if (d != null) list.Add(ParseDevice(d));
            lastRowKey = result["has_more"]?.GetValue<bool>() == true ? result["last_row_key"]?.GetValue<string>() : null;
        } while (lastRowKey != null);
        return list;
    }

    private static CloudDevice ParseDevice(JsonNode d)
    {
        var status = new Dictionary<string, JsonElement>();
        foreach (var s in d["status"]?.AsArray() ?? [])
        {
            var code = s?["code"]?.GetValue<string>();
            if (code != null) status[code] = JsonSerializer.SerializeToElement(s!["value"]);
        }
        return new CloudDevice(
            Str(d, "id")!, Str(d, "name") ?? Str(d, "id")!, Str(d, "local_key"), Str(d, "category"),
            Str(d, "product_id"), Str(d, "product_name"), Str(d, "uid"), Str(d, "owner_id"),
            d["online"]?.GetValue<bool>() ?? false, Str(d, "icon"), status);
    }

    /// <summary>The device's "thing model": every DP with id, code, type and range.</summary>
    public async Task<List<DpSchema>> GetDeviceSchemaAsync(string deviceId, CancellationToken ct = default)
    {
        var result = await RequestAsync(HttpMethod.Get, $"/v2.0/cloud/thing/{deviceId}/model", null, ct);
        var modelText = result["model"]?.GetValue<string>();
        var schema = new List<DpSchema>();
        if (modelText == null) return schema;
        var model = JsonNode.Parse(modelText)!;
        foreach (var service in model["services"]?.AsArray() ?? [])
        foreach (var p in service?["properties"]?.AsArray() ?? [])
        {
            if (p == null) continue;
            var spec = p["typeSpec"];
            var dp = new DpSchema
            {
                Id = p["abilityId"]?.ToString() ?? "",
                Code = Str(p, "code") ?? "",
                Name = Str(p, "name"),
                Mode = Str(p, "accessMode") ?? "rw",
                Type = (Str(spec, "type") ?? "raw") switch
                {
                    "bool" => DpType.Bool,
                    "value" => DpType.Value,
                    "enum" => DpType.Enum,
                    "string" => DpType.String,
                    "bitmap" => DpType.Bitmap,
                    "struct" or "array" => DpType.Json,
                    _ => DpType.Raw,
                },
            };
            if (dp.Type == DpType.Value)
            {
                dp.Min = Long(spec, "min") ?? 0;
                dp.Max = Long(spec, "max") ?? 100;
                dp.Step = Math.Max(1, Long(spec, "step") ?? 1);
                dp.Scale = (int)(Long(spec, "scale") ?? 0);
                dp.Unit = Str(spec, "unit");
            }
            if (spec?["range"] is JsonArray range)
                dp.Range = range.Select(r => r?.ToString() ?? "").ToList();
            if (spec?["label"] is JsonArray labels)
                dp.Range = labels.Select(r => r?.ToString() ?? "").ToList();
            schema.Add(dp);
        }
        return schema;
    }

    /// <summary>Current DP values keyed by code (via the device shadow, values in raw DP format).</summary>
    public async Task<Dictionary<string, JsonElement>> GetStatusAsync(string deviceId, CancellationToken ct = default)
    {
        var dict = new Dictionary<string, JsonElement>();
        try
        {
            var result = await RequestAsync(HttpMethod.Get, $"/v2.0/cloud/thing/{deviceId}/shadow/properties", null, ct);
            foreach (var p in result["properties"]?.AsArray() ?? [])
            {
                var code = Str(p, "code");
                if (code != null) dict[code] = JsonSerializer.SerializeToElement(p!["value"]);
            }
        }
        catch (TuyaCloudException)
        {
            var result = await RequestAsync(HttpMethod.Get, $"/v1.0/iot-03/devices/{deviceId}/status", null, ct);
            foreach (var s in result.AsArray())
            {
                var code = Str(s, "code");
                if (code != null) dict[code] = JsonSerializer.SerializeToElement(s!["value"]);
            }
        }
        return dict;
    }

    /// <summary>Sends DP values (by code) to the device through the cloud.</summary>
    public async Task SendPropertiesAsync(string deviceId, IReadOnlyDictionary<string, object?> values, CancellationToken ct = default)
    {
        try
        {
            var body = new JsonObject { ["properties"] = JsonSerializer.Serialize(values) };
            await RequestAsync(HttpMethod.Post, $"/v2.0/cloud/thing/{deviceId}/shadow/properties/issue", body, ct);
        }
        catch (TuyaCloudException)
        {
            // Fallback: standard instruction set. Colours are sent as {"h","s","v"} objects there.
            var cmds = new JsonArray();
            foreach (var (code, value) in values)
            {
                JsonNode? v = value is string s && code.StartsWith("colour_data", StringComparison.Ordinal) && Lights.HsvColor.TryParse(s, out var hsv)
                    ? JsonNode.Parse(hsv.ToCloudJson(code.EndsWith("_v2", StringComparison.Ordinal)))
                    : JsonSerializer.SerializeToNode(value);
                cmds.Add(new JsonObject { ["code"] = code, ["value"] = v });
            }
            await RequestAsync(HttpMethod.Post, $"/v1.0/iot-03/devices/{deviceId}/commands", new JsonObject { ["commands"] = cmds }, ct);
        }
    }

    // ---------------------------------------------------------------- homes, rooms, scenes

    public async Task<List<CloudHome>> GetHomesAsync(string uid, CancellationToken ct = default)
    {
        var result = await RequestAsync(HttpMethod.Get, $"/v1.0/users/{uid}/homes", null, ct);
        return result.AsArray().Where(h => h != null)
            .Select(h => new CloudHome(h!["home_id"]!.ToString(), Str(h, "name") ?? "Home")).ToList();
    }

    public async Task<List<CloudRoom>> GetRoomsAsync(string homeId, CancellationToken ct = default)
    {
        var result = await RequestAsync(HttpMethod.Get, $"/v1.0/homes/{homeId}/rooms", null, ct);
        var rooms = new List<CloudRoom>();
        foreach (var r in result["rooms"]?.AsArray() ?? [])
        {
            if (r == null) continue;
            var roomId = r["room_id"]!.ToString();
            var devs = await RequestAsync(HttpMethod.Get, $"/v1.0/homes/{homeId}/rooms/{roomId}/devices", null, ct);
            rooms.Add(new CloudRoom(roomId, Str(r, "name") ?? "Room", devs.AsArray().Select(d => Str(d, "id")).OfType<string>().ToList()));
        }
        return rooms;
    }

    public async Task<List<CloudScene>> GetScenesAsync(string homeId, CancellationToken ct = default)
    {
        var result = await RequestAsync(HttpMethod.Get, $"/v1.1/homes/{homeId}/scenes", null, ct);
        return result.AsArray().Where(s => s != null)
            .Select(s => new CloudScene(Str(s, "scene_id")!, Str(s, "name") ?? "Scene", homeId, Str(s, "status") != "2" && s!["enabled"]?.GetValue<bool>() != false))
            .ToList();
    }

    public Task TriggerSceneAsync(string homeId, string sceneId, CancellationToken ct = default) =>
        RequestAsync(HttpMethod.Post, $"/v1.0/homes/{homeId}/scenes/{sceneId}/trigger", null, ct);

    public async Task<List<CloudAutomation>> GetAutomationsAsync(string homeId, CancellationToken ct = default)
    {
        var result = await RequestAsync(HttpMethod.Get, $"/v1.0/homes/{homeId}/automations", null, ct);
        return result.AsArray().Where(a => a != null)
            .Select(a => new CloudAutomation(Str(a, "automation_id")!, Str(a, "name") ?? "Automation", homeId, a!["enabled"]?.GetValue<bool>() ?? false))
            .ToList();
    }

    public Task SetAutomationEnabledAsync(string homeId, string automationId, bool enabled, CancellationToken ct = default) =>
        RequestAsync(HttpMethod.Put, $"/v1.0/homes/{homeId}/automations/{automationId}/actions/{(enabled ? "enable" : "disable")}", null, ct);

    // ---------------------------------------------------------------- transport & signing

    public async Task<JsonNode> RequestAsync(HttpMethod method, string pathAndQuery, JsonNode? body, CancellationToken ct = default, bool retried = false)
    {
        await EnsureTokenAsync(ct);
        try
        {
            return await SendSignedAsync(method, pathAndQuery, body, _accessToken, ct);
        }
        catch (TuyaCloudException ex) when (!retried && ex.Code is 1010 or 1011)
        {
            _accessToken = null; // token expired/invalid
            return await RequestAsync(method, pathAndQuery, body, ct, retried: true);
        }
    }

    private async Task EnsureTokenAsync(CancellationToken ct)
    {
        if (_accessToken != null && DateTimeOffset.UtcNow < _tokenExpiry) return;
        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_accessToken != null && DateTimeOffset.UtcNow < _tokenExpiry) return;
            var result = await SendSignedAsync(HttpMethod.Get, "/v1.0/token?grant_type=1", null, null, ct);
            _accessToken = result["access_token"]!.GetValue<string>();
            var expire = result["expire_time"]?.GetValue<long>() ?? 3600;
            _tokenExpiry = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expire - 60));
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task<JsonNode> SendSignedAsync(HttpMethod method, string pathAndQuery, JsonNode? body, string? token, CancellationToken ct)
    {
        var bodyText = body?.ToJsonString() ?? "";
        var t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var nonce = Guid.NewGuid().ToString("N");
        var sign = Sign(_clientId, _secret, token, t, nonce, method.Method, bodyText, pathAndQuery);

        using var req = new HttpRequestMessage(method, pathAndQuery);
        req.Headers.Add("client_id", _clientId);
        req.Headers.Add("sign", sign);
        req.Headers.Add("t", t);
        req.Headers.Add("nonce", nonce);
        req.Headers.Add("sign_method", "HMAC-SHA256");
        if (token != null) req.Headers.Add("access_token", token);
        if (body != null)
        {
            req.Content = new StringContent(bodyText, Encoding.UTF8);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        using var resp = await _http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        JsonNode? json;
        try { json = JsonNode.Parse(text); }
        catch (JsonException) { throw new TuyaCloudException($"HTTP {(int)resp.StatusCode}: {text}"); }
        if (json?["success"]?.GetValue<bool>() != true)
        {
            int? code = json?["code"]?.GetValue<int>();
            throw new TuyaCloudException($"Tuya cloud error {code}: {json?["msg"]}", code);
        }
        return json["result"] ?? new JsonObject();
    }

    /// <summary>Tuya OpenAPI request signature (HMAC-SHA256, upper-case hex).</summary>
    public static string Sign(string clientId, string secret, string? accessToken, string t, string nonce,
        string method, string body, string pathAndQuery)
    {
        var contentHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
        var stringToSign = $"{method}\n{contentHash}\n\n{CanonicalUrl(pathAndQuery)}";
        var str = clientId + (accessToken ?? "") + t + nonce + stringToSign;
        return Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(str)));
    }

    /// <summary>Path plus query parameters sorted by key, as required by the signature.</summary>
    public static string CanonicalUrl(string pathAndQuery)
    {
        int q = pathAndQuery.IndexOf('?');
        if (q < 0) return pathAndQuery;
        var parts = pathAndQuery[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries)
            .OrderBy(p => p.Split('=')[0], StringComparer.Ordinal);
        return pathAndQuery[..q] + "?" + string.Join("&", parts);
    }

    private static string? Str(JsonNode? n, string key) => n?[key] is JsonValue v ? v.ToString() : null;

    private static long? Long(JsonNode? n, string key) =>
        n?[key] is JsonValue v && long.TryParse(v.ToString(), out var l) ? l : null;
}
