using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LedvanceControl.Core.Local;

/// <summary>Where to find the Matter bridge (a Node.js script) and where it keeps its fabric.</summary>
public sealed record MatterBridgeOptions(string NodeExecutable, string ScriptPath, string StorageDirectory)
{
    /// <summary>Base64 AES-256 key that encrypts the bridge's storage; sent over stdin when it starts.</summary>
    public string? StorageKey { get; init; }

    /// <summary>
    /// Finds Node.js and the bridge script. Node is taken from LEDVANCE_NODE, a copy bundled under
    /// <c>matter-bridge\node\node.exe</c>, or the PATH. Returns null when either piece is missing.
    /// </summary>
    public static MatterBridgeOptions? Locate(string appDirectory, string storageDirectory)
    {
        var script = Path.Combine(appDirectory, "matter-bridge", "bridge.mjs");
        if (!File.Exists(script)) return null;
        var node = FindNode(appDirectory);
        return node == null ? null : new MatterBridgeOptions(node, script, storageDirectory);
    }

    private static string? FindNode(string appDirectory)
    {
        if (Environment.GetEnvironmentVariable("LEDVANCE_NODE") is { Length: > 0 } env && File.Exists(env)) return env;
        var bundled = Path.Combine(appDirectory, "matter-bridge", "node", "node.exe");
        if (File.Exists(bundled)) return bundled;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), OperatingSystem.IsWindows() ? "node.exe" : "node");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { /* malformed PATH entry */ }
        }
        return null;
    }
}

public sealed class MatterException(string message) : Exception(message);

/// <summary>Light capabilities of a Matter device, as reported by the bridge.</summary>
public sealed class MatterCapabilities
{
    public bool OnOff { get; set; } = true;
    public bool Level { get; set; }
    public bool Hs { get; set; }
    public bool Xy { get; set; }
    public bool Ct { get; set; }
    public int CtMinKelvin { get; set; } = 2700;
    public int CtMaxKelvin { get; set; } = 6500;
    public bool HasColour => Hs || Xy;
}

/// <summary>Normalised light state: level/saturation 0-100 %, hue 0-360°, colour temperature in Kelvin.</summary>
public sealed class MatterLightState
{
    public bool? On { get; set; }
    public int? Level { get; set; }
    public int? Hue { get; set; }
    public int? Saturation { get; set; }
    public int? ColorTempKelvin { get; set; }
    /// <summary>"hs", "xy" or "ct".</summary>
    public string? ColorMode { get; set; }
}

public sealed class MatterNodeInfo
{
    public string NodeId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? VendorName { get; set; }
    public string? ProductName { get; set; }
    public string? SerialNumber { get; set; }
    public string? SoftwareVersion { get; set; }
    public bool Connected { get; set; }
    public string? Error { get; set; }
    public MatterCapabilities? Capabilities { get; set; }
    public MatterLightState? State { get; set; }
}

public sealed class MatterCommissionableDevice
{
    public string? DeviceIdentifier { get; set; }
    public int? Discriminator { get; set; }
    public int? VendorId { get; set; }
    public int? ProductId { get; set; }
    public string? DeviceName { get; set; }
    public List<string> Addresses { get; set; } = [];

    /// <summary>"4489 / 2756" — one sortable column in the UI.</summary>
    public string VendorProduct => $"{VendorId} / {ProductId}";
    /// <summary>First advertised address (IPv4 first), for display and sorting.</summary>
    public string? FirstAddress => Addresses.FirstOrDefault(a => a.Contains('.')) ?? Addresses.FirstOrDefault();
    /// <summary>Numeric sort key for the address column.</summary>
    public string AddressSortKey => Model.SortKeys.Ip(FirstAddress);
}

/// <summary>
/// Runs the Node.js Matter controller (matter-bridge/bridge.mjs) as a child process and talks to it
/// with newline-delimited JSON over stdio. The process is started on first use and restarted on the
/// next request if it dies. Device state pushed by Matter subscriptions arrives as events.
/// </summary>
public sealed class MatterBridgeClient : IAsyncDisposable
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly MatterBridgeOptions _options;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly Queue<string> _stderrTail = new();
    private Process? _process;
    private TaskCompletionSource? _ready;
    private long _nextId;
    private bool _disposed;

    public MatterBridgeClient(MatterBridgeOptions options) => _options = options;

    public MatterBridgeOptions Options => _options;
    public bool IsRunning => _process is { HasExited: false };

    /// <summary>Raised (on a worker thread) with the node id and its latest normalised state.</summary>
    public event Action<string, MatterLightState>? StateReceived;
    /// <summary>Raised (on a worker thread) with the node id and "connected", "disconnected", "reconnecting", "waiting" or "decommissioned".</summary>
    public event Action<string, string>? ConnectionChanged;

    // ---------------------------------------------------------------- typed commands

    public async Task<MatterNodeInfo> CommissionAsync(string pairingCode, string? ip = null, CancellationToken ct = default)
    {
        var args = new JsonObject { ["pairingCode"] = pairingCode.Trim() };
        if (!string.IsNullOrWhiteSpace(ip))
        {
            // accepts "192.168.1.20" or "192.168.1.20:5540" (IPv4); anything else is passed through as-is
            var text = ip.Trim();
            var colon = text.LastIndexOf(':');
            if (colon > 0 && text.Count(c => c == ':') == 1 && int.TryParse(text[(colon + 1)..], out var port))
            {
                args["ip"] = text[..colon];
                args["port"] = port;
            }
            else args["ip"] = text;
        }
        // Commissioning includes discovery, PASE, certificate exchange and CASE: allow plenty of time.
        var r = await RequestAsync("commission", args, TimeSpan.FromSeconds(120), ct);
        return r.Deserialize<MatterNodeInfo>(Json) ?? throw new MatterException("Empty commissioning result.");
    }

    public async Task<IReadOnlyList<MatterNodeInfo>> GetNodesAsync(CancellationToken ct = default) =>
        (await RequestAsync("nodes", null, null, ct)).Deserialize<List<MatterNodeInfo>>(Json) ?? [];

    public async Task<MatterNodeInfo> GetAsync(string nodeId, CancellationToken ct = default) =>
        (await RequestAsync("get", new JsonObject { ["nodeId"] = nodeId }, null, ct)).Deserialize<MatterNodeInfo>(Json)
        ?? throw new MatterException("Empty response.");

    /// <summary>Sends a command built by <see cref="MatterMapping.CodesToCommand"/> (on, level, hue, saturation, colorTempKelvin).</summary>
    public async Task<MatterLightState> SetAsync(string nodeId, JsonObject command, CancellationToken ct = default)
    {
        var args = (JsonObject)command.DeepClone();
        args["nodeId"] = nodeId;
        return (await RequestAsync("set", args, null, ct)).Deserialize<MatterLightState>(Json) ?? new MatterLightState();
    }

    public Task RemoveAsync(string nodeId, CancellationToken ct = default) =>
        RequestAsync("remove", new JsonObject { ["nodeId"] = nodeId }, TimeSpan.FromSeconds(30), ct);

    public async Task<IReadOnlyList<MatterCommissionableDevice>> DiscoverAsync(TimeSpan duration, CancellationToken ct = default) =>
        (await RequestAsync("discover", new JsonObject { ["timeoutSeconds"] = (int)duration.TotalSeconds }, duration + TimeSpan.FromSeconds(15), ct))
        .Deserialize<List<MatterCommissionableDevice>>(Json) ?? [];

    // ---------------------------------------------------------------- transport

    public async Task<JsonNode?> RequestAsync(string cmd, JsonObject? args, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await EnsureStartedAsync(ct);

        var id = Interlocked.Increment(ref _nextId);
        var request = args != null ? (JsonObject)args.DeepClone() : new JsonObject();
        request["id"] = id;
        request["cmd"] = cmd;
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await _writeGate.WaitAsync(ct);
            try
            {
                await _process!.StandardInput.WriteLineAsync(request.ToJsonString());
                await _process.StandardInput.FlushAsync(ct);
            }
            finally { _writeGate.Release(); }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout ?? DefaultTimeout);
            try
            {
                return await tcs.Task.WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new MatterException($"The Matter bridge did not answer '{cmd}' in time.");
            }
        }
        catch (IOException ex)
        {
            throw new MatterException("The Matter bridge stopped: " + ex.Message + StderrSuffix());
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task EnsureStartedAsync(CancellationToken ct)
    {
        if (IsRunning && _ready!.Task.IsCompletedSuccessfully) return;
        await _startGate.WaitAsync(ct);
        try
        {
            if (!IsRunning)
            {
                if (string.IsNullOrEmpty(_options.StorageKey))
                    throw new MatterException("The Matter bridge needs a storage key (no secure secret store?).");
                Security.FilePermissions.SecureDirectory(_options.StorageDirectory);
                var psi = new ProcessStartInfo(_options.NodeExecutable)
                {
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = new UTF8Encoding(false),
                    StandardErrorEncoding = new UTF8Encoding(false),
                    StandardInputEncoding = new UTF8Encoding(false),
                    WorkingDirectory = Path.GetDirectoryName(_options.ScriptPath)!,
                };
                psi.ArgumentList.Add(_options.ScriptPath);
                psi.ArgumentList.Add("--storage");
                psi.ArgumentList.Add(_options.StorageDirectory);

                _ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_stderrTail) _stderrTail.Clear();
                var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
                p.ErrorDataReceived += (_, e) => { if (e.Data != null) AddStderr(e.Data); };
                p.Exited += (_, _) => OnExited(p);
                try
                {
                    p.Start();
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    throw new MatterException($"Could not start Node.js ({_options.NodeExecutable}): {ex.Message}");
                }
                p.BeginErrorReadLine();
                _process = p;
                _ = Task.Run(() => ReadLoopAsync(p));

                // First line: the storage key (over stdin, never on the command line or in the environment,
                // where other processes could read it). The bridge refuses to start without it.
                var init = new JsonObject { ["cmd"] = "init", ["storageKey"] = _options.StorageKey };
                await p.StandardInput.WriteLineAsync(init.ToJsonString());
                await p.StandardInput.FlushAsync(ct);
            }

            using var startCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            startCts.CancelAfter(TimeSpan.FromSeconds(45));
            try
            {
                await _ready!.Task.WaitAsync(startCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new MatterException("The Matter bridge did not start in time." + StderrSuffix());
            }
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task ReadLoopAsync(Process p)
    {
        try
        {
            while (await p.StandardOutput.ReadLineAsync() is { } line)
            {
                if (line.Length == 0 || line[0] != '{') continue;
                JsonObject? msg;
                try { msg = JsonNode.Parse(line) as JsonObject; }
                catch (JsonException) { continue; }
                if (msg != null) Dispatch(msg);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // process went away; OnExited fails the pending requests
        }
    }

    internal void Dispatch(JsonObject msg)
    {
        if (msg["event"]?.GetValue<string>() is { } ev)
        {
            var nodeId = msg["nodeId"]?.ToString();
            switch (ev)
            {
                case "ready":
                    _ready?.TrySetResult();
                    break;
                case "state" when nodeId != null:
                    if (msg["state"].Deserialize<MatterLightState>(Json) is { } st) StateReceived?.Invoke(nodeId, st);
                    break;
                case "connection" when nodeId != null:
                    ConnectionChanged?.Invoke(nodeId, msg["status"]?.ToString() ?? "");
                    break;
            }
            return;
        }

        if (msg["id"] is not JsonValue idValue || !idValue.TryGetValue<long>(out var id)) return;
        if (!_pending.TryGetValue(id, out var tcs)) return;
        if (msg["ok"]?.GetValue<bool>() == true) tcs.TrySetResult(msg["result"]?.DeepClone());
        else tcs.TrySetException(new MatterException(msg["error"]?.ToString() ?? "Unknown Matter bridge error."));
    }

    private void OnExited(Process p)
    {
        if (!ReferenceEquals(p, _process)) return;
        var reason = new MatterException($"The Matter bridge exited (code {SafeExitCode(p)})." + StderrSuffix());
        _ready?.TrySetException(reason);
        foreach (var (_, tcs) in _pending) tcs.TrySetException(reason);
    }

    private static string SafeExitCode(Process p)
    {
        try { return p.ExitCode.ToString(); } catch { return "?"; }
    }

    private void AddStderr(string line)
    {
        lock (_stderrTail)
        {
            _stderrTail.Enqueue(line);
            while (_stderrTail.Count > 40) _stderrTail.Dequeue();
        }
    }

    /// <summary>The last lines the bridge wrote to stderr (for diagnostics).</summary>
    public string RecentLog()
    {
        lock (_stderrTail) return string.Join(Environment.NewLine, _stderrTail);
    }

    private string StderrSuffix()
    {
        string last;
        lock (_stderrTail) last = _stderrTail.LastOrDefault(l => l.Contains("Error", StringComparison.OrdinalIgnoreCase)) ?? _stderrTail.LastOrDefault() ?? "";
        return last.Length > 0 ? " " + last.Trim() : "";
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        var p = _process;
        if (p == null) return;
        try
        {
            if (!p.HasExited)
            {
                // closing stdin asks the bridge to close its sessions and exit cleanly
                try { p.StandardInput.Close(); } catch (IOException) { }
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await p.WaitForExitAsync(cts.Token); }
                catch (OperationCanceledException) { p.Kill(entireProcessTree: true); }
            }
        }
        catch (InvalidOperationException) { /* never started / already gone */ }
        finally
        {
            p.Dispose();
        }
    }
}
