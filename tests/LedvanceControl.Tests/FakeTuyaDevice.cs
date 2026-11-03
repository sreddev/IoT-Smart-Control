using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LedvanceControl.Core.Local;

namespace LedvanceControl.Tests;

/// <summary>Minimal device-side implementation of the Tuya LAN protocol, for tests.</summary>
public sealed class FakeTuyaDevice : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly byte[] _localKey;
    private readonly TuyaVersion _version;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public FakeTuyaDevice(string localKey, TuyaVersion version)
    {
        _localKey = Encoding.UTF8.GetBytes(localKey);
        _version = version;
        _listener.Start();
        _loop = Task.Run(AcceptLoop);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public JsonObject Dps { get; } = new() { ["20"] = false, ["21"] = "white", ["22"] = 500 };

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { return; }
            _ = Task.Run(() => Serve(c));
        }
    }

    private async Task Serve(TcpClient client)
    {
        using var _ = client;
        var s = client.GetStream();
        byte[] key = _localKey;
        byte[]? localNonce = null;
        var remoteNonce = RandomNumberGenerator.GetBytes(16);
        uint seq = 100;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var frame = await ReadFrame(s);
                var (cmd, payload) = Decode(frame, key);
                switch ((TuyaCommand)cmd)
                {
                    case TuyaCommand.SessKeyNegStart:
                        localNonce = payload[..16];
                        await Send(s, ++seq, TuyaCommand.SessKeyNegResp, [.. remoteNonce, .. TuyaCodec.HmacSha256(_localKey, localNonce)], key, raw: true);
                        break;
                    case TuyaCommand.SessKeyNegFinish:
                        if (!payload.AsSpan(0, 32).SequenceEqual(TuyaCodec.HmacSha256(_localKey, remoteNonce))) return;
                        key = TuyaCodec.DeriveSessionKey(_version, _localKey, localNonce!, remoteNonce);
                        break;
                    case TuyaCommand.DpQuery or TuyaCommand.DpQueryNew:
                        await Send(s, ++seq, (TuyaCommand)cmd, Encoding.UTF8.GetBytes(new JsonObject { ["dps"] = Dps.DeepClone() }.ToJsonString()), key);
                        break;
                    case TuyaCommand.Control or TuyaCommand.ControlNew:
                        var json = JsonNode.Parse(TuyaCodec.ExtractJson(payload)!)!;
                        var dps = (json["dps"] ?? json["data"]!["dps"])!.AsObject();
                        foreach (var (k, v) in dps) Dps[k] = v?.DeepClone();
                        var push = _version >= TuyaVersion.V34
                            ? new JsonObject { ["protocol"] = 4, ["data"] = new JsonObject { ["dps"] = dps.DeepClone() } }
                            : new JsonObject { ["dps"] = dps.DeepClone() };
                        await Send(s, ++seq, TuyaCommand.Status, Encoding.UTF8.GetBytes(push.ToJsonString()), key, header: true);
                        break;
                    case TuyaCommand.HeartBeat:
                        await Send(s, ++seq, TuyaCommand.HeartBeat, [], key);
                        break;
                }
            }
        }
        catch { /* client went away */ }
    }

    private (uint cmd, byte[] payload) Decode(byte[] frame, byte[] key)
    {
        if (_version == TuyaVersion.V35)
        {
            var m = TuyaCodec.Unpack6699(frame, key);
            return ((uint)m.Command, TuyaCodec.StripVersionHeader(m.Payload));
        }
        var msg = TuyaCodec.Unpack55AA(frame, _version == TuyaVersion.V34 ? key : null);
        if (msg.Command is TuyaCommand.SessKeyNegStart or TuyaCommand.SessKeyNegFinish)
            return ((uint)msg.Command, TuyaCodec.EcbDecrypt(_localKey, msg.Payload));
        return ((uint)msg.Command, TuyaCodec.DecryptPayload55AA(msg.Payload, _version, key));
    }

    private async Task Send(NetworkStream s, uint seq, TuyaCommand cmd, byte[] json, byte[] key, bool raw = false, bool header = false)
    {
        byte[] retcode = [0, 0, 0, 0];
        byte[] frame;
        if (_version == TuyaVersion.V35)
        {
            byte[] body = header ? [.. TuyaCodec.VersionHeader(_version), .. json] : json;
            frame = TuyaCodec.Pack6699(seq, cmd, [.. retcode, .. body], key);
        }
        else
        {
            byte[] enc;
            if (raw) enc = TuyaCodec.EcbEncrypt(key, json);
            else if (json.Length == 0) enc = [];
            else if (_version == TuyaVersion.V33) enc = header ? [.. TuyaCodec.VersionHeader(_version), .. TuyaCodec.EcbEncrypt(key, json)] : TuyaCodec.EcbEncrypt(key, json);
            else enc = TuyaCodec.EcbEncrypt(key, header ? [.. TuyaCodec.VersionHeader(_version), .. json] : json);
            frame = TuyaCodec.Pack55AA(seq, cmd, [.. retcode, .. enc], _version == TuyaVersion.V34 ? key : null);
        }
        await s.WriteAsync(frame);
    }

    private static async Task<byte[]> ReadFrame(NetworkStream s)
    {
        var head = new byte[18];
        await s.ReadExactlyAsync(head.AsMemory(0, 16));
        int hl = 16;
        if (BinaryPrimitives.ReadUInt32BigEndian(head) == TuyaCodec.Prefix6699)
        {
            await s.ReadExactlyAsync(head.AsMemory(16, 2));
            hl = 18;
        }
        int total = TuyaCodec.FrameLength(head.AsSpan(0, hl));
        var f = new byte[total];
        head.AsSpan(0, hl).CopyTo(f);
        await s.ReadExactlyAsync(f.AsMemory(hl));
        return f;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try { await _loop; } catch { }
    }
}
