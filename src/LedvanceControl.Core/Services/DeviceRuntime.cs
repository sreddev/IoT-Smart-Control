using System.Text;
using System.Text.Json;
using LedvanceControl.Core.Cloud;
using LedvanceControl.Core.Lights;
using LedvanceControl.Core.Local;
using LedvanceControl.Core.Model;

namespace LedvanceControl.Core.Services;

public enum Transport { None, Lan, Cloud, SmartThings }

/// <summary>
/// Live state of one device plus the logic that chooses LAN or cloud to talk to it.
/// State is kept by DP code (falls back to DP id when the code is unknown).
/// </summary>
public sealed class DeviceRuntime : IAsyncDisposable
{
    private readonly DeviceManager _manager;
    private readonly object _gate = new();
    private readonly Dictionary<string, object?> _state = new();
    private TuyaLocalClient? _local;
    private string? _localSignature;

    internal DeviceRuntime(DeviceManager manager, DeviceConfig config)
    {
        _manager = manager;
        Config = config;
        Light = LightProfile.From(config.Schema);
    }

    public DeviceConfig Config { get; }
    public string Id => Config.Id;
    public LightProfile? Light { get; private set; }
    public Transport LastTransport { get; private set; }
    public bool Online { get; private set; }
    public string? LastError { get; private set; }

    public bool CanUseLan => !string.IsNullOrEmpty(Config.Ip) && Config.LocalKey is { Length: 16 };
    public bool CanUseCloud => Config.InCloud && _manager.Cloud != null;
    public bool CanUseSmartThings => Config.SmartThingsId != null && _manager.SmartThings != null;

    /// <summary>Raised on any state change (worker thread).</summary>
    public event Action<DeviceRuntime>? Changed;

    public object? Get(string code)
    {
        lock (_gate) return _state.GetValueOrDefault(code);
    }

    public IReadOnlyDictionary<string, object?> Snapshot()
    {
        lock (_gate) return new Dictionary<string, object?>(_state);
    }

    public bool? IsOn
    {
        get
        {
            var code = Light?.SwitchCode ?? Config.Schema.FirstOrDefault(s => s.Type == DpType.Bool && s.CanWrite)?.Code;
            return code != null && Get(code) is bool b ? b : null;
        }
    }

    public string? PowerCode => Light?.SwitchCode ?? Config.Schema.FirstOrDefault(s => s.Type == DpType.Bool && s.CanWrite)?.Code;

    // ---------------------------------------------------------------- operations

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (CanUseSmartThings)
        {
            try
            {
                var main = await _manager.SmartThings!.GetStatusAsync(Config.SmartThingsId!, ct);
                var health = await _manager.SmartThings.GetHealthAsync(Config.SmartThingsId!, ct);
                ApplyValues(SmartThingsMapping.StatusToCodes(main), Transport.SmartThings);
                Online = health.Online;
                Changed?.Invoke(this);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                SetError(ex);
            }
            return;
        }

        Exception? lanError = null;
        if (CanUseLan && (Config.PreferLocal || !CanUseCloud))
        {
            try
            {
                var dps = await GetLocal().QueryAsync(ct);
                EnsureSchema(dps);
                ApplyLocal(dps, Transport.Lan);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lanError = ex;
            }
        }
        if (CanUseCloud)
        {
            try
            {
                var status = await _manager.Cloud!.GetStatusAsync(Id, ct);
                ApplyCloud(status);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                SetError(ex);
                return;
            }
        }
        SetError(lanError ?? new InvalidOperationException("No local key/IP and the device is not in the cloud project."));
    }

    public async Task SetAsync(IReadOnlyDictionary<string, object?> byCode, CancellationToken ct = default)
    {
        if (CanUseSmartThings)
        {
            var commands = SmartThingsMapping.CodesToCommands(byCode);
            if (commands.Count > 0) await _manager.SmartThings!.SendCommandsAsync(Config.SmartThingsId!, commands, ct);
            ApplyValues(byCode, Transport.SmartThings);
            return;
        }

        Exception? lanError = null;
        if (CanUseLan && (Config.PreferLocal || !CanUseCloud))
        {
            try
            {
                var dps = byCode.ToDictionary(kv => CodeToId(kv.Key), kv => kv.Value);
                await GetLocal().SetDpsAsync(dps, ct);
                ApplyValues(byCode, Transport.Lan);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lanError = ex;
            }
        }
        if (CanUseCloud)
        {
            await _manager.Cloud!.SendPropertiesAsync(Id, byCode, ct);
            ApplyValues(byCode, Transport.Cloud);
            return;
        }
        SetError(lanError ?? new InvalidOperationException("Device not reachable."));
        throw lanError ?? new InvalidOperationException($"{Config.Name}: not reachable over LAN or cloud.");
    }

    public Task SetAsync(string code, object? value, CancellationToken ct = default) =>
        SetAsync(new Dictionary<string, object?> { [code] = value }, ct);

    public Task SetPowerAsync(bool on, CancellationToken ct = default)
    {
        var code = PowerCode ?? throw new InvalidOperationException($"{Config.Name} has no power switch DP.");
        return SetAsync(code, on, ct);
    }

    /// <summary>Runs a connectivity + status check and returns a human-readable report.</summary>
    public async Task<string> DiagnosticsAsync(CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Device: {Config.Name}");
        sb.AppendLine($"Id: {Config.Id}");

        if (CanUseSmartThings)
        {
            sb.AppendLine("Channel: SmartThings cloud");
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var health = await _manager.SmartThings!.GetHealthAsync(Config.SmartThingsId!, ct);
                var main = await _manager.SmartThings.GetStatusAsync(Config.SmartThingsId!, ct);
                sw.Stop();
                sb.AppendLine($"Health: {(health.Online ? "ONLINE" : "OFFLINE")}" + (health.LastUpdated != null ? $" (last update {health.LastUpdated})" : ""));
                sb.AppendLine($"Round-trip: {sw.ElapsedMilliseconds} ms");
                sb.AppendLine("Capabilities / current values:");
                foreach (var (code, value) in SmartThingsMapping.StatusToCodes(main))
                    sb.AppendLine($"  {code} = {value}");
                ApplyValues(SmartThingsMapping.StatusToCodes(main), Transport.SmartThings);
                Online = health.Online;
                Changed?.Invoke(this);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                sb.AppendLine("ERROR: " + ex.Message);
            }
            return sb.ToString();
        }

        sb.AppendLine($"LAN: {(CanUseLan ? $"{Config.Ip} (protocol {Config.Version})" : "not configured (needs IP + 16-char local key)")}");
        sb.AppendLine($"Tuya cloud: {(CanUseCloud ? "available" : "not available")}");
        if (CanUseLan)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var dps = await GetLocal().QueryAsync(ct);
                sw.Stop();
                sb.AppendLine($"LAN query OK in {sw.ElapsedMilliseconds} ms, {dps.Count} data point(s):");
                foreach (var (id, el) in dps) sb.AppendLine($"  DP {id} ({IdToCode(id)}) = {el}");
                ApplyLocal(dps, Transport.Lan);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                sb.AppendLine("LAN query FAILED: " + ex.Message);
            }
        }
        if (CanUseCloud)
        {
            try
            {
                var status = await _manager.Cloud!.GetStatusAsync(Id, ct);
                sb.AppendLine($"Cloud status OK, {status.Count} value(s).");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                sb.AppendLine("Cloud status FAILED: " + ex.Message);
            }
        }
        return sb.ToString();
    }

    /// <summary>Re-reads the schema-derived light profile after the schema changed.</summary>
    public void SchemaChanged()
    {
        Light = LightProfile.From(Config.Schema);
        _localSignature = null;
        Changed?.Invoke(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_local != null) await _local.DisposeAsync();
        _local = null;
    }

    // ---------------------------------------------------------------- internals

    private TuyaLocalClient GetLocal()
    {
        var sig = $"{Config.Ip}|{Config.LocalKey}|{Config.Version}";
        if (_local != null && _localSignature == sig) return _local;
        var old = _local;
        _ = old?.DisposeAsync().AsTask();
        _local = new TuyaLocalClient(Config.Id, Config.Ip!, Config.LocalKey!, TuyaVersionExtensions.Parse(Config.Version))
        {
            KnownDpIds = Config.Schema.Count > 0 ? Config.Schema.Select(s => s.Id).ToList() : ["1", "20"],
        };
        _local.DpsReceived += dps => ApplyLocal(dps, Transport.Lan);
        _local.Disconnected += _ => { };
        _localSignature = sig;
        return _local;
    }

    private void EnsureSchema(IReadOnlyDictionary<string, JsonElement> dps)
    {
        if (Config.Schema.Count > 0) return;
        Config.Schema = DefaultSchemas.Guess(Config.Category, dps);
        SchemaChanged();
        _manager.Save();
    }

    private string CodeToId(string code) => Config.Schema.FirstOrDefault(s => s.Code == code)?.Id ?? code;
    private string IdToCode(string id) => Config.Schema.FirstOrDefault(s => s.Id == id)?.Code ?? id;

    private void ApplyLocal(IReadOnlyDictionary<string, JsonElement> dps, Transport t) =>
        ApplyValues(dps.ToDictionary(kv => IdToCode(kv.Key), kv => FromJson(kv.Value)), t);

    private void ApplyCloud(IReadOnlyDictionary<string, JsonElement> status)
    {
        var values = new Dictionary<string, object?>();
        foreach (var (code, el) in status)
        {
            var v = FromJson(el);
            // the cloud may report colours as {"h","s","v"}; keep the raw DP hex format internally
            if (code.StartsWith("colour_data", StringComparison.Ordinal) && Light != null)
            {
                var text = el.ValueKind == JsonValueKind.Object ? el.GetRawText() : v as string;
                if (HsvColor.TryParse(text, out var c)) v = Light.EncodeColour(c);
            }
            values[code] = v;
        }
        ApplyValues(values, Transport.Cloud);
    }

    private void ApplyValues(IReadOnlyDictionary<string, object?> values, Transport t)
    {
        lock (_gate)
        {
            foreach (var (k, v) in values) _state[k] = v;
        }
        LastTransport = t;
        Online = true;
        LastError = null;
        Changed?.Invoke(this);
    }

    private void SetError(Exception ex)
    {
        Online = false;
        LastTransport = Transport.None;
        LastError = ex.Message;
        Changed?.Invoke(this);
    }

    public static object? FromJson(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => el.TryGetInt64(out var l) ? (object)l : el.GetDouble(),
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => el.GetRawText(),
    };
}
