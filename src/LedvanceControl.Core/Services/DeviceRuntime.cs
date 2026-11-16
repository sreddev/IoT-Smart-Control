using System.Text;
using System.Text.Json;
using LedvanceControl.Core.Cloud;
using LedvanceControl.Core.Lights;
using LedvanceControl.Core.Local;
using LedvanceControl.Core.Model;

namespace LedvanceControl.Core.Services;

public enum Transport { None, Lan, Cloud, SmartThings, Matter }

/// <summary>
/// Live state of one device plus the logic that chooses LAN or cloud to talk to it.
/// State is kept by DP code (falls back to DP id when the code is unknown).
/// </summary>
public sealed class DeviceRuntime : IAsyncDisposable
{
    private readonly DeviceManager _manager;
    private readonly object _gate = new();
    private readonly Dictionary<string, object?> _state = new();
    /// <summary>
    /// Values this app just sent, by code, and until when they win over what the device/cloud reports:
    /// a status read or a late confirmation can carry the value from before the change, which used to
    /// flip a switch or slider back (so a click looked ignored).
    /// </summary>
    private readonly Dictionary<string, (object? Value, long Until)> _pending = new();
    private long _writeSeq;
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
    public bool IsMatter => Config.MatterNodeId != null;

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

    /// <summary>True while <see cref="RefreshAsync"/> is asking the device for its state (UI shows a spinner).</summary>
    public bool IsRefreshing { get; private set; }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        IsRefreshing = true;
        Changed?.Invoke(this);
        try
        {
            await RefreshCoreAsync(ct);
        }
        finally
        {
            IsRefreshing = false;
            Changed?.Invoke(this);
        }
    }

    private async Task RefreshCoreAsync(CancellationToken ct)
    {
        if (IsMatter)
        {
            try
            {
                var info = await RequireMatter().GetAsync(Config.MatterNodeId!, ct);
                if (info.Connected && info.State != null) ApplyMatterState(info.State);
                else ApplyMatterConnection("disconnected");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                SetError(ex);
            }
            return;
        }

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
        // Show the change at once and hold it against stale reports while it travels to the device.
        var hold = IsMatter || (!CanUseSmartThings && CanUseLan && (Config.PreferLocal || !CanUseCloud)) ? LocalHold : CloudHold;
        var seq = BeginWrite(byCode, hold);
        try
        {
            await SendAsync(byCode, seq, ct);
        }
        catch
        {
            EndWriteFailed(byCode);
            throw;
        }
    }

    /// <summary>How long a value just sent wins over reports for LAN / Matter devices (they answer fast).</summary>
    internal static TimeSpan LocalHold { get; set; } = TimeSpan.FromSeconds(2);
    /// <summary>How long it wins for cloud devices (SmartThings / Tuya cloud confirm within ~0.5-3 s).</summary>
    internal static TimeSpan CloudHold { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>After the last SmartThings change, when to check that the device really applied it.</summary>
    internal static TimeSpan VerifyAfter { get; set; } = TimeSpan.FromSeconds(4);

    private async Task SendAsync(IReadOnlyDictionary<string, object?> byCode, long seq, CancellationToken ct)
    {
        if (IsMatter)
        {
            var command = MatterMapping.CodesToCommand(byCode);
            if (command.Count == 0) return;
            try
            {
                await RequireMatter().SetAsync(Config.MatterNodeId!, command, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                SetError(ex);
                throw;
            }
            Reached(Transport.Matter);
            return;
        }

        if (CanUseSmartThings)
        {
            var commands = SmartThingsMapping.CodesToCommands(byCode);
            if (commands.Count > 0)
            {
                await _manager.SmartThings!.SendCommandsAsync(Config.SmartThingsId!, commands, ct);
                _ = VerifySmartThingsAsync(commands, seq);
            }
            Reached(Transport.SmartThings);
            return;
        }

        Exception? lanError = null;
        if (CanUseLan && (Config.PreferLocal || !CanUseCloud))
        {
            try
            {
                var dps = byCode.ToDictionary(kv => CodeToId(kv.Key), kv => kv.Value);
                await GetLocal().SetDpsAsync(dps, ct);
                Reached(Transport.Lan);
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
            Reached(Transport.Cloud);
            return;
        }
        SetError(lanError ?? new InvalidOperationException("Device not reachable."));
        throw lanError ?? new InvalidOperationException($"{Config.Name}: not reachable over LAN or cloud.");
    }

    /// <summary>
    /// SmartThings acknowledges a command when its cloud accepts it, not when the device applied it, and
    /// a quick series of changes can lose one on the way. Once the changes have settled, read what the
    /// device reports and resend power / brightness once if they did not take.
    /// </summary>
    private async Task VerifySmartThingsAsync(System.Text.Json.Nodes.JsonArray commands, long seq)
    {
        try
        {
            await Task.Delay(VerifyAfter);
            if (Interlocked.Read(ref _writeSeq) != seq) return; // a newer change verifies itself
            var st = _manager.SmartThings;
            if (st == null) return;
            var main = await st.GetStatusAsync(Config.SmartThingsId!);
            var missing = SmartThingsMapping.NotApplied(commands, main);
            if (missing.Count == 0 || Interlocked.Read(ref _writeSeq) != seq) return;
            await st.SendCommandsAsync(Config.SmartThingsId!, missing);
            Resent?.Invoke(this, missing.Count);
        }
        catch (Exception)
        {
            // best effort: the next refresh shows the real state
        }
    }

    /// <summary>Raised when a SmartThings change had not reached the device and was sent again.</summary>
    public event Action<DeviceRuntime, int>? Resent;

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

        if (IsMatter)
        {
            sb.AppendLine($"Channel: Matter over the LAN (node {Config.MatterNodeId})");
            try
            {
                var bridge = RequireMatter();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var info = await bridge.GetAsync(Config.MatterNodeId!, ct);
                sw.Stop();
                sb.AppendLine($"Product: {info.VendorName} {info.ProductName} (firmware {info.SoftwareVersion})");
                if (info.SerialNumber != null) sb.AppendLine($"Serial: {info.SerialNumber}");
                sb.AppendLine($"Session: {(info.Connected ? "connected" : "not connected")}, read in {sw.ElapsedMilliseconds} ms");
                if (info.Capabilities is { } c)
                    sb.AppendLine($"Capabilities: on/off{(c.Level ? ", dimming" : "")}{(c.Hs ? ", hue/saturation" : "")}{(c.Xy ? ", XY colour" : "")}{(c.Ct ? $", white {c.CtMinKelvin}-{c.CtMaxKelvin} K" : "")}");
                if (info.State is { } s)
                {
                    sb.AppendLine("Current values:");
                    foreach (var (code, value) in MatterMapping.StateToCodes(s, Config.Schema))
                        sb.AppendLine($"  {code} = {value}");
                    ApplyMatterState(s);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                sb.AppendLine("ERROR: " + ex.Message);
                if (_manager.Matter?.RecentLog() is { Length: > 0 } log)
                    sb.AppendLine().AppendLine("Bridge log (most recent):").AppendLine(log);
            }
            return sb.ToString();
        }

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

    // ---------------------------------------------------------------- Matter

    /// <summary>Applies state pushed by (or read from) the Matter bridge.</summary>
    internal void ApplyMatterState(MatterLightState state) =>
        ApplyValues(MatterMapping.StateToCodes(state, Config.Schema), Transport.Matter);

    /// <summary>Reflects a Matter session change ("connected", "disconnected", "reconnecting", …).</summary>
    internal void ApplyMatterConnection(string status)
    {
        switch (status)
        {
            case "disconnected" or "waiting":
                SetError(new MatterException("Not reachable over Matter (bulb powered off or not on Wi-Fi)."));
                break;
            case "decommissioned":
                SetError(new MatterException("This PC was removed from the device's Matter fabric. Pair it again."));
                break;
        }
    }

    private MatterBridgeClient RequireMatter() => _manager.Matter
        ?? throw new MatterException("Matter needs Node.js (nodejs.org, LTS). Install it and restart the app.");

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

    private long BeginWrite(IReadOnlyDictionary<string, object?> values, TimeSpan hold)
    {
        var until = Environment.TickCount64 + (long)hold.TotalMilliseconds;
        lock (_gate)
        {
            foreach (var (k, v) in values)
            {
                _state[k] = v;
                _pending[k] = (v, until);
            }
        }
        Changed?.Invoke(this);
        return Interlocked.Increment(ref _writeSeq);
    }

    /// <summary>The send failed: stop holding these values so the next report shows the real state.</summary>
    private void EndWriteFailed(IReadOnlyDictionary<string, object?> values)
    {
        lock (_gate)
        {
            foreach (var (k, v) in values)
                if (_pending.TryGetValue(k, out var p) && SameValue(p.Value, v)) _pending.Remove(k);
        }
    }

    private void Reached(Transport t)
    {
        LastTransport = t;
        Online = true;
        LastError = null;
        Changed?.Invoke(this);
    }

    internal static bool SameValue(object? a, object? b)
    {
        if (Equals(a, b)) return true;
        if (a is string sa && b is string sb) return string.Equals(sa, sb, StringComparison.OrdinalIgnoreCase);
        if (a is null || b is null || a is bool || b is bool || a is string || b is string) return false;
        try
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            return Convert.ToDouble(a, inv) == Convert.ToDouble(b, inv);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Applies values reported by the device or cloud; values this app just sent still win for a moment.</summary>
    private void ApplyValues(IReadOnlyDictionary<string, object?> values, Transport t)
    {
        var now = Environment.TickCount64;
        lock (_gate)
        {
            foreach (var (k, v) in values)
            {
                if (_pending.TryGetValue(k, out var p))
                {
                    if (SameValue(p.Value, v) || now >= p.Until) _pending.Remove(k); // confirmed, or held long enough
                    else continue;                                                // stale: keep what was sent
                }
                _state[k] = v;
            }
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
