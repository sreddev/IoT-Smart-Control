using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LedvanceControl.Core.Local;

/// <summary>
/// Persistent TCP connection to a single Tuya device on the LAN (port 6668).
/// Handles session key negotiation (3.4/3.5), heartbeats, status queries and DP writes.
/// Unsolicited status updates pushed by the device are raised through <see cref="DpsReceived"/>.
/// </summary>
public sealed class TuyaLocalClient : IAsyncDisposable
{
    public const int DefaultPort = 6668;
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);

    private readonly string _deviceId;
    private readonly string _host;
    private readonly int _port;
    private readonly byte[] _localKey;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private byte[] _key;
    private uint _seq;
    private CancellationTokenSource? _loopCts;
    private TaskCompletionSource<Dictionary<string, JsonElement>>? _pendingQuery;
    private bool _device22;

    public TuyaLocalClient(string deviceId, string host, string localKey, TuyaVersion version, int port = DefaultPort)
    {
        _deviceId = deviceId;
        _host = host;
        _port = port;
        _localKey = Encoding.UTF8.GetBytes(localKey);
        if (_localKey.Length != 16) throw new ArgumentException("Tuya local key must be 16 characters.", nameof(localKey));
        _key = _localKey;
        Version = version;
        // 22 character ids on 3.3 firmware are "device22" units that need a different query
        _device22 = version == TuyaVersion.V33 && deviceId.Length == 22;
    }

    public TuyaVersion Version { get; }
    public bool IsConnected => _tcp?.Connected == true;
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>DP ids used when a "device22" query has to enumerate DPs explicitly.</summary>
    public IReadOnlyList<string> KnownDpIds { get; set; } = ["1"];

    /// <summary>Raised (on a worker thread) whenever the device reports DP values.</summary>
    public event Action<IReadOnlyDictionary<string, JsonElement>>? DpsReceived;

    /// <summary>Raised when the connection drops.</summary>
    public event Action<Exception?>? Disconnected;

    // ---------------------------------------------------------------- public API

    public async Task<Dictionary<string, JsonElement>> QueryAsync(CancellationToken ct = default)
    {
        try
        {
            return await QueryOnceAsync(ct);
        }
        catch (InvalidOperationException) when (_device22)
        {
            return await QueryOnceAsync(ct);
        }
    }

    private async Task<Dictionary<string, JsonElement>> QueryOnceAsync(CancellationToken ct)
    {
        await EnsureConnectedAsync(ct);
        var tcs = new TaskCompletionSource<Dictionary<string, JsonElement>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingQuery = tcs;
        var (cmd, json) = BuildQuery();
        await SendAsync(cmd, json, ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        await using var _ = timeout.Token.Register(() => tcs.TrySetException(new TimeoutException("Device did not answer the status query.")));
        return await tcs.Task;
    }

    public async Task SetDpsAsync(IReadOnlyDictionary<string, object?> dps, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct);
        long t = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        object body = Version >= TuyaVersion.V34
            ? new { protocol = 5, t, data = new { dps } }
            : new { devId = _deviceId, uid = _deviceId, t = t.ToString(), dps };
        await SendAsync(Version >= TuyaVersion.V34 ? TuyaCommand.ControlNew : TuyaCommand.Control, JsonSerializer.SerializeToUtf8Bytes(body), ct);
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _connectLock.WaitAsync(ct);
        try
        {
            if (IsConnected) return;
            Close();
            var tcp = new TcpClient { NoDelay = true };
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(Timeout);
                await tcp.ConnectAsync(_host, _port, cts.Token);
            }
            _tcp = tcp;
            _stream = tcp.GetStream();
            _key = _localKey;
            if (Version >= TuyaVersion.V34)
                await NegotiateSessionKeyAsync(ct);
            _loopCts = new CancellationTokenSource();
            _ = Task.Run(() => ReceiveLoopAsync(_loopCts.Token));
            _ = Task.Run(() => HeartbeatLoopAsync(_loopCts.Token));
        }
        catch
        {
            Close();
            throw;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        Close();
        _sendLock.Dispose();
        _connectLock.Dispose();
        return ValueTask.CompletedTask;
    }

    // ---------------------------------------------------------------- internals

    private Task EnsureConnectedAsync(CancellationToken ct) => IsConnected ? Task.CompletedTask : ConnectAsync(ct);

    private (TuyaCommand, byte[]) BuildQuery()
    {
        long t = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (Version >= TuyaVersion.V34)
            return (TuyaCommand.DpQueryNew, "{}"u8.ToArray());
        if (_device22)
        {
            var dps = KnownDpIds.ToDictionary(id => id, _ => (object?)null);
            return (TuyaCommand.ControlNew, JsonSerializer.SerializeToUtf8Bytes(new { devId = _deviceId, uid = _deviceId, t = t.ToString(), dps }));
        }
        return (TuyaCommand.DpQuery, JsonSerializer.SerializeToUtf8Bytes(new { gwId = _deviceId, devId = _deviceId, uid = _deviceId, t = t.ToString() }));
    }

    private byte[] BuildFrame(TuyaCommand cmd, byte[] payload, byte[] key)
    {
        uint seq = Interlocked.Increment(ref _seq);
        if (Version == TuyaVersion.V35)
        {
            byte[] plain = TuyaCodec.HasNoVersionHeader(cmd) ? payload : [.. TuyaCodec.VersionHeader(Version), .. payload];
            return TuyaCodec.Pack6699(seq, cmd, plain, key);
        }
        var enc = TuyaCodec.EncryptPayload55AA(cmd, payload, Version, key);
        return TuyaCodec.Pack55AA(seq, cmd, enc, Version == TuyaVersion.V34 ? key : null);
    }

    private async Task SendAsync(TuyaCommand cmd, byte[] payload, CancellationToken ct)
    {
        var frame = BuildFrame(cmd, payload, _key);
        await _sendLock.WaitAsync(ct);
        try
        {
            var stream = _stream ?? throw new IOException("Not connected");
            await stream.WriteAsync(frame, ct);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            Close(ex);
            throw;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task NegotiateSessionKeyAsync(CancellationToken ct)
    {
        var localNonce = RandomNumberGenerator.GetBytes(16);
        await _stream!.WriteAsync(BuildFrame(TuyaCommand.SessKeyNegStart, localNonce, _localKey), ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        var frame = await ReadFrameAsync(_stream, cts.Token);
        byte[] resp;
        if (Version == TuyaVersion.V35)
        {
            resp = TuyaCodec.Unpack6699(frame, _localKey).Payload;
        }
        else
        {
            var msg = TuyaCodec.Unpack55AA(frame, _localKey);
            resp = TuyaCodec.EcbDecrypt(_localKey, msg.Payload);
        }
        if (resp.Length < 48) throw new CryptographicException("Invalid session key negotiation response (wrong local key?)");
        var remoteNonce = resp[..16];
        if (!resp.AsSpan(16, 32).SequenceEqual(TuyaCodec.HmacSha256(_localKey, localNonce)))
            throw new CryptographicException("Session key negotiation failed: HMAC mismatch (wrong local key?)");

        await _stream.WriteAsync(BuildFrame(TuyaCommand.SessKeyNegFinish, TuyaCodec.HmacSha256(_localKey, remoteNonce), _localKey), ct);
        _key = TuyaCodec.DeriveSessionKey(Version, _localKey, localNonce, remoteNonce);
    }

    private static async Task<byte[]> ReadFrameAsync(NetworkStream stream, CancellationToken ct)
    {
        var header = new byte[18];
        await stream.ReadExactlyAsync(header.AsMemory(0, 16), ct);
        if (BinaryPrimitives.ReadUInt32BigEndian(header) == TuyaCodec.Prefix6699)
            await stream.ReadExactlyAsync(header.AsMemory(16, 2), ct);
        else if (BinaryPrimitives.ReadUInt32BigEndian(header) != TuyaCodec.Prefix55AA)
            throw new InvalidDataException("Lost frame synchronisation");
        int headerLen = BinaryPrimitives.ReadUInt32BigEndian(header) == TuyaCodec.Prefix6699 ? 18 : 16;
        int total = TuyaCodec.FrameLength(header.AsSpan(0, headerLen));
        if (total > 1 << 20) throw new InvalidDataException("Frame too large");
        var frame = new byte[total];
        header.AsSpan(0, headerLen).CopyTo(frame);
        await stream.ReadExactlyAsync(frame.AsMemory(headerLen), ct);
        return frame;
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var stream = _stream!;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await ReadFrameAsync(stream, ct);
                HandleFrame(frame);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Close(ex);
        }
    }

    private void HandleFrame(byte[] frame)
    {
        byte[] payload;
        TuyaMessage msg;
        if (Version == TuyaVersion.V35)
        {
            msg = TuyaCodec.Unpack6699(frame, _key);
            payload = TuyaCodec.StripVersionHeader(msg.Payload);
        }
        else
        {
            msg = TuyaCodec.Unpack55AA(frame, Version == TuyaVersion.V34 ? _key : null);
            payload = TuyaCodec.DecryptPayload55AA(msg.Payload, Version, _key);
        }
        if (msg.Command == TuyaCommand.HeartBeat) return;

        var json = TuyaCodec.ExtractJson(payload);
        if (json == null)
        {
            if (Encoding.UTF8.GetString(payload).Contains("data unvalid", StringComparison.OrdinalIgnoreCase) && !_device22)
            {
                _device22 = true;
                _pendingQuery?.TrySetException(new InvalidOperationException("Device requires device22 query mode; retry."));
            }
            return;
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        JsonElement dpsEl = default;
        bool found = root.TryGetProperty("dps", out dpsEl) ||
                     (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("dps", out dpsEl));
        if (!found || dpsEl.ValueKind != JsonValueKind.Object) return;

        var dps = dpsEl.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        _pendingQuery?.TrySetResult(dps);
        DpsReceived?.Invoke(dps);
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(HeartbeatInterval, ct);
                byte[] body = Version >= TuyaVersion.V34
                    ? "{}"u8.ToArray()
                    : JsonSerializer.SerializeToUtf8Bytes(new { gwId = _deviceId, devId = _deviceId });
                await SendAsync(TuyaCommand.HeartBeat, body, ct);
            }
        }
        catch { /* connection closed */ }
    }

    private void Close(Exception? reason = null)
    {
        var wasConnected = _tcp != null;
        _loopCts?.Cancel();
        _loopCts = null;
        try { _stream?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
        _stream = null;
        _tcp = null;
        _pendingQuery?.TrySetException(reason ?? new IOException("Connection closed"));
        if (wasConnected) Disconnected?.Invoke(reason);
    }
}
