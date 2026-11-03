using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LedvanceControl.Core.Cloud;

public sealed class SmartThingsException(string message, int? status = null) : Exception(message)
{
    public int? Status { get; } = status;
}

public sealed record StDevice(string DeviceId, string Label, IReadOnlyList<string> Capabilities, string? RoomId, string? LocationId);
public sealed record StRoom(string RoomId, string Name);
public sealed record StHealth(bool Online, string? LastUpdated);
public sealed record StTokens(string AccessToken, string RefreshToken, DateTimeOffset Expiry);

/// <summary>
/// Client for the SmartThings REST API (https://api.smartthings.com/v1) using OAuth 2.0.
/// Access tokens last ~24 h and are renewed automatically with the refresh token.
/// </summary>
public sealed class SmartThingsClient : IDisposable
{
    public const string Base = "https://api.smartthings.com/v1";
    public const string AuthorizeUrl = "https://api.smartthings.com/v1/oauth/authorize";
    public const string TokenUrl = "https://api.smartthings.com/v1/oauth/token";
    public const string DefaultScope = "r:devices:* w:devices:* x:devices:* r:locations:*";

    private readonly HttpClient _http;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _accessToken;
    private string? _refreshToken;
    private DateTimeOffset _expiry;

    public SmartThingsClient(string clientId, string clientSecret, string? refreshToken, HttpMessageHandler? handler = null)
    {
        _clientId = clientId;
        _clientSecret = clientSecret;
        _refreshToken = refreshToken;
        _http = handler == null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(20);
    }

    public string? RefreshToken => _refreshToken;
    public bool HasToken => _refreshToken != null;

    /// <summary>Raised after the tokens change, so the caller can persist the new refresh token.</summary>
    public event Action<StTokens>? TokensChanged;

    public void Dispose() => _http.Dispose();

    // ---------------------------------------------------------------- OAuth

    public static string BuildAuthorizeUrl(string clientId, string redirectUri, string state, string scope = DefaultScope)
    {
        var q = $"client_id={Uri.EscapeDataString(clientId)}&response_type=code&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                $"&scope={Uri.EscapeDataString(scope)}&state={Uri.EscapeDataString(state)}";
        return AuthorizeUrl + "?" + q;
    }

    public async Task ExchangeCodeAsync(string code, string redirectUri, CancellationToken ct = default)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = _clientId,
            ["redirect_uri"] = redirectUri,
        };
        await PostTokenAsync(form, ct);
    }

    private async Task EnsureTokenAsync(CancellationToken ct)
    {
        if (_accessToken != null && DateTimeOffset.UtcNow < _expiry) return;
        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_accessToken != null && DateTimeOffset.UtcNow < _expiry) return;
            if (_refreshToken == null) throw new SmartThingsException("Not connected to SmartThings (no refresh token). Use Connect in Settings.");
            await PostTokenAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = _refreshToken,
                ["client_id"] = _clientId,
            }, ct);
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task PostTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, TokenUrl) { Content = new FormUrlEncodedContent(form) };
        var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{_clientId}:{_clientSecret}"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await _http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new SmartThingsException($"SmartThings token request failed (HTTP {(int)resp.StatusCode}): {text}", (int)resp.StatusCode);
        var json = JsonNode.Parse(text)!;
        _accessToken = json["access_token"]!.GetValue<string>();
        _refreshToken = json["refresh_token"]?.GetValue<string>() ?? _refreshToken;
        var expiresIn = json["expires_in"]?.GetValue<long>() ?? 86400;
        _expiry = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn - 120));
        TokensChanged?.Invoke(new StTokens(_accessToken, _refreshToken!, _expiry));
    }

    // ---------------------------------------------------------------- REST

    private async Task<JsonNode> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct, bool retried = false)
    {
        await EnsureTokenAsync(ct);
        using var req = new HttpRequestMessage(method, Base + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        if (body != null)
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized && !retried)
        {
            _accessToken = null; // force refresh
            return await SendAsync(method, path, body, ct, retried: true);
        }
        if (!resp.IsSuccessStatusCode)
            throw new SmartThingsException($"SmartThings {method} {path} failed (HTTP {(int)resp.StatusCode}): {text}", (int)resp.StatusCode);
        return string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text) ?? new JsonObject();
    }

    public async Task<List<StDevice>> GetDevicesAsync(CancellationToken ct = default)
    {
        var result = await SendAsync(HttpMethod.Get, "/devices", null, ct);
        var list = new List<StDevice>();
        foreach (var d in result["items"]?.AsArray() ?? [])
        {
            if (d == null) continue;
            var caps = new List<string>();
            foreach (var comp in d["components"]?.AsArray() ?? [])
                if (comp?["id"]?.GetValue<string>() == "main")
                    foreach (var c in comp["capabilities"]?.AsArray() ?? [])
                        if (c?["id"]?.GetValue<string>() is { } id) caps.Add(id);
            list.Add(new StDevice(
                d["deviceId"]!.GetValue<string>(),
                d["label"]?.GetValue<string>() ?? d["name"]?.GetValue<string>() ?? "Device",
                caps, d["roomId"]?.GetValue<string>(), d["locationId"]?.GetValue<string>()));
        }
        return list;
    }

    /// <summary>The "main" component's capability states as a JsonObject (capability → attribute → {value,...}).</summary>
    public async Task<JsonObject> GetStatusAsync(string deviceId, CancellationToken ct = default)
    {
        var result = await SendAsync(HttpMethod.Get, $"/devices/{deviceId}/status", null, ct);
        return result["components"]?["main"]?.AsObject() ?? new JsonObject();
    }

    public async Task<StHealth> GetHealthAsync(string deviceId, CancellationToken ct = default)
    {
        var result = await SendAsync(HttpMethod.Get, $"/devices/{deviceId}/health", null, ct);
        return new StHealth(result["state"]?.GetValue<string>() == "ONLINE", result["lastUpdatedDate"]?.GetValue<string>());
    }

    public Task SendCommandsAsync(string deviceId, JsonArray commands, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"/devices/{deviceId}/commands", new JsonObject { ["commands"] = commands }, ct);

    public async Task<List<StRoom>> GetRoomsAsync(string locationId, CancellationToken ct = default)
    {
        try
        {
            var result = await SendAsync(HttpMethod.Get, $"/locations/{locationId}/rooms", null, ct);
            return (result["items"]?.AsArray() ?? [])
                .Where(r => r != null)
                .Select(r => new StRoom(r!["roomId"]!.GetValue<string>(), r["name"]?.GetValue<string>() ?? "Room")).ToList();
        }
        catch (SmartThingsException)
        {
            return [];
        }
    }
}
