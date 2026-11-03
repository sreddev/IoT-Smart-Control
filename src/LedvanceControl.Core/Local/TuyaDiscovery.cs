using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace LedvanceControl.Core.Local;

/// <summary>A device found through its UDP broadcast.</summary>
public sealed record DiscoveredDevice(string Id, string Ip, string Version, string? ProductKey, bool Encrypted);

/// <summary>
/// Listens for the broadcasts Tuya devices send every few seconds:
/// port 6666 (3.1, plaintext), 6667 (3.3/3.4, AES-ECB) and 7000 (3.5, AES-GCM).
/// </summary>
public static class TuyaDiscovery
{
    private static readonly int[] Ports = [6666, 6667, 7000];

    public static async Task<IReadOnlyList<DiscoveredDevice>> ScanAsync(TimeSpan duration, Action<DiscoveredDevice>? onFound = null, CancellationToken ct = default)
    {
        var found = new ConcurrentDictionary<string, DiscoveredDevice>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(duration);
        var clients = new List<UdpClient>();
        try
        {
            foreach (var port in Ports)
            {
                var udp = new UdpClient { ExclusiveAddressUse = false, EnableBroadcast = true };
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                try
                {
                    udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
                }
                catch (SocketException)
                {
                    udp.Dispose();
                    continue; // port in use by another app, skip it
                }
                clients.Add(udp);
            }
            if (clients.Count == 0) throw new InvalidOperationException("Could not bind any Tuya discovery port (6666/6667/7000).");

            await Task.WhenAll(clients.Select(async udp =>
            {
                while (!cts.IsCancellationRequested)
                {
                    UdpReceiveResult r;
                    try { r = await udp.ReceiveAsync(cts.Token); }
                    catch (OperationCanceledException) { break; }
                    catch (SocketException) { continue; }
                    var dev = TryDecode(r.Buffer, r.RemoteEndPoint.Address.ToString());
                    if (dev != null && found.TryAdd(dev.Id, dev)) onFound?.Invoke(dev);
                }
            }));
        }
        finally
        {
            foreach (var c in clients) c.Dispose();
        }
        return found.Values.ToList();
    }

    /// <summary>Decodes one broadcast datagram, or returns null if it is not a recognised Tuya broadcast.</summary>
    public static DiscoveredDevice? TryDecode(byte[] data, string fromIp)
    {
        try
        {
            byte[] payload;
            uint prefix = BinaryPrimitives.ReadUInt32BigEndian(data);
            if (prefix == TuyaCodec.Prefix6699)
            {
                payload = TuyaCodec.Unpack6699(data, TuyaCodec.UdpKey).Payload;
            }
            else if (prefix == TuyaCodec.Prefix55AA)
            {
                var msg = TuyaCodec.Unpack55AA(data, null, verify: false);
                payload = msg.Payload;
                if (payload.Length > 0 && payload[0] != (byte)'{' && payload.Length % 16 == 0)
                    payload = TuyaCodec.EcbDecrypt(TuyaCodec.UdpKey, payload);
            }
            else
            {
                return null;
            }

            var json = TuyaCodec.ExtractJson(payload);
            if (json == null) return null;
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? id = root.TryGetProperty("gwId", out var g) ? g.GetString() : null;
            if (string.IsNullOrEmpty(id)) return null;
            string ip = root.TryGetProperty("ip", out var ipEl) ? ipEl.GetString() ?? fromIp : fromIp;
            string version = root.TryGetProperty("version", out var v) ? v.GetString() ?? "3.3" : "3.3";
            string? pk = root.TryGetProperty("productKey", out var p) ? p.GetString() : null;
            bool enc = root.TryGetProperty("encrypt", out var e) && e.ValueKind == JsonValueKind.True;
            return new DiscoveredDevice(id, ip, version, pk, enc);
        }
        catch
        {
            return null;
        }
    }
}
