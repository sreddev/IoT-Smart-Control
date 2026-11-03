using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace LedvanceControl.Core.Local;

/// <summary>A decoded Tuya LAN frame.</summary>
public sealed record TuyaMessage(uint Seq, TuyaCommand Command, uint? RetCode, byte[] Payload)
{
    public string PayloadText => Encoding.UTF8.GetString(Payload);
}

/// <summary>
/// Framing and crypto for the Tuya LAN protocol (3.1, 3.3, 3.4 and 3.5).
/// 3.1-3.4 use the 0x55AA frame (CRC32 or HMAC-SHA256 trailer, AES-128-ECB payload);
/// 3.5 uses the 0x6699 frame with AES-128-GCM.
/// </summary>
public static class TuyaCodec
{
    public const uint Prefix55AA = 0x000055AA;
    public const uint Suffix55AA = 0x0000AA55;
    public const uint Prefix6699 = 0x00006699;
    public const uint Suffix6699 = 0x00009966;

    /// <summary>Key used to encrypt UDP discovery broadcasts (md5 of a well-known string).</summary>
    public static readonly byte[] UdpKey = MD5.HashData("yGAdlopoPVldABfn"u8.ToArray());

    /// <summary>Commands that never carry the "3.x" + 12 zero byte version header.</summary>
    public static bool HasNoVersionHeader(TuyaCommand cmd) => cmd is
        TuyaCommand.DpQuery or TuyaCommand.DpQueryNew or TuyaCommand.UpdateDps or TuyaCommand.HeartBeat or
        TuyaCommand.SessKeyNegStart or TuyaCommand.SessKeyNegResp or TuyaCommand.SessKeyNegFinish or
        TuyaCommand.LanExtStream;

    public static byte[] VersionHeader(TuyaVersion v)
    {
        var h = new byte[15];
        Encoding.ASCII.GetBytes(v.ToText()).CopyTo(h, 0);
        return h;
    }

    // ---------------------------------------------------------------- AES helpers

    public static byte[] EcbEncrypt(byte[] key, byte[] data, bool pad = true)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.EncryptEcb(data, pad ? PaddingMode.PKCS7 : PaddingMode.None);
    }

    public static byte[] EcbDecrypt(byte[] key, byte[] data, bool pad = true)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.DecryptEcb(data, pad ? PaddingMode.PKCS7 : PaddingMode.None);
    }

    public static byte[] HmacSha256(byte[] key, byte[] data) => HMACSHA256.HashData(key, data);

    // ---------------------------------------------------------------- 0x55AA frames

    /// <summary>Builds a 0x55AA frame. If <paramref name="hmacKey"/> is set (3.4) the trailer is HMAC-SHA256, else CRC32.</summary>
    public static byte[] Pack55AA(uint seq, TuyaCommand cmd, byte[] payload, byte[]? hmacKey)
    {
        int trailer = (hmacKey != null ? 32 : 4) + 4;
        var buf = new byte[16 + payload.Length + trailer];
        var s = buf.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(s[0..], Prefix55AA);
        BinaryPrimitives.WriteUInt32BigEndian(s[4..], seq);
        BinaryPrimitives.WriteUInt32BigEndian(s[8..], (uint)cmd);
        BinaryPrimitives.WriteUInt32BigEndian(s[12..], (uint)(payload.Length + trailer));
        payload.CopyTo(buf, 16);
        int end = 16 + payload.Length;
        if (hmacKey != null)
            HmacSha256(hmacKey, buf[..end]).CopyTo(buf, end);
        else
            BinaryPrimitives.WriteUInt32BigEndian(s[end..], Crc32.Compute(s[..end]));
        BinaryPrimitives.WriteUInt32BigEndian(s[^4..], Suffix55AA);
        return buf;
    }

    /// <summary>Total frame length if <paramref name="data"/> starts with a complete header, else -1.</summary>
    public static int FrameLength(ReadOnlySpan<byte> data)
    {
        if (data.Length < 16) return -1;
        uint prefix = BinaryPrimitives.ReadUInt32BigEndian(data);
        if (prefix == Prefix55AA)
            return 16 + (int)BinaryPrimitives.ReadUInt32BigEndian(data[12..]);
        if (prefix == Prefix6699)
            return data.Length < 18 ? -1 : 18 + (int)BinaryPrimitives.ReadUInt32BigEndian(data[14..]) + 4;
        throw new InvalidDataException($"Unknown frame prefix 0x{prefix:X8}");
    }

    /// <summary>Parses a 0x55AA frame. Payload is returned still encrypted. Verifies CRC/HMAC.</summary>
    public static TuyaMessage Unpack55AA(ReadOnlySpan<byte> frame, byte[]? hmacKey, bool verify = true)
    {
        if (BinaryPrimitives.ReadUInt32BigEndian(frame) != Prefix55AA) throw new InvalidDataException("Bad 55AA prefix");
        uint seq = BinaryPrimitives.ReadUInt32BigEndian(frame[4..]);
        var cmd = (TuyaCommand)BinaryPrimitives.ReadUInt32BigEndian(frame[8..]);
        int len = (int)BinaryPrimitives.ReadUInt32BigEndian(frame[12..]);
        int trailer = (hmacKey != null ? 32 : 4) + 4;
        int end = 16 + len - trailer;
        if (end < 16 || 16 + len > frame.Length) throw new InvalidDataException("Bad 55AA length");
        if (verify)
        {
            if (hmacKey != null)
            {
                var expected = HmacSha256(hmacKey, frame[..end].ToArray());
                if (!frame.Slice(end, 32).SequenceEqual(expected)) throw new CryptographicException("HMAC mismatch");
            }
            else if (BinaryPrimitives.ReadUInt32BigEndian(frame[end..]) != Crc32.Compute(frame[..end]))
            {
                throw new InvalidDataException("CRC mismatch");
            }
        }
        var body = frame[16..end];
        uint? ret = null;
        if (LooksLikeRetCode(body))
        {
            ret = BinaryPrimitives.ReadUInt32BigEndian(body);
            body = body[4..];
        }
        return new TuyaMessage(seq, cmd, ret, body.ToArray());
    }

    // Device->client frames normally start with a 4 byte return code (0 or a small error code).
    private static bool LooksLikeRetCode(ReadOnlySpan<byte> body) =>
        body.Length >= 4 && body[0] == 0 && body[1] == 0 && body[2] == 0;

    // ---------------------------------------------------------------- 0x6699 frames (3.5)

    public static byte[] Pack6699(uint seq, TuyaCommand cmd, byte[] plaintext, byte[] key, byte[]? iv = null)
    {
        iv ??= RandomNumberGenerator.GetBytes(12);
        int len = 12 + plaintext.Length + 16;
        var buf = new byte[18 + len + 4];
        var s = buf.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(s[0..], Prefix6699);
        // bytes 4..5 reserved (0)
        BinaryPrimitives.WriteUInt32BigEndian(s[6..], seq);
        BinaryPrimitives.WriteUInt32BigEndian(s[10..], (uint)cmd);
        BinaryPrimitives.WriteUInt32BigEndian(s[14..], (uint)len);
        iv.CopyTo(buf, 18);
        using var gcm = new AesGcm(key, 16);
        gcm.Encrypt(iv, plaintext, s.Slice(30, plaintext.Length), s.Slice(30 + plaintext.Length, 16), s[4..18]);
        BinaryPrimitives.WriteUInt32BigEndian(s[^4..], Suffix6699);
        return buf;
    }

    /// <summary>Parses and decrypts a 0x6699 frame.</summary>
    public static TuyaMessage Unpack6699(ReadOnlySpan<byte> frame, byte[] key)
    {
        if (BinaryPrimitives.ReadUInt32BigEndian(frame) != Prefix6699) throw new InvalidDataException("Bad 6699 prefix");
        uint seq = BinaryPrimitives.ReadUInt32BigEndian(frame[6..]);
        var cmd = (TuyaCommand)BinaryPrimitives.ReadUInt32BigEndian(frame[10..]);
        int len = (int)BinaryPrimitives.ReadUInt32BigEndian(frame[14..]);
        if (len < 28 || 18 + len + 4 > frame.Length) throw new InvalidDataException("Bad 6699 length");
        var iv = frame.Slice(18, 12);
        var ct = frame.Slice(30, len - 28);
        var tag = frame.Slice(30 + ct.Length, 16);
        var pt = new byte[ct.Length];
        using var gcm = new AesGcm(key, 16);
        gcm.Decrypt(iv, ct, tag, pt, frame[4..18]);
        ReadOnlySpan<byte> body = pt;
        uint? ret = null;
        if (body.Length >= 4 && body[0] == 0 && body[1] == 0 && body[2] == 0)
        {
            ret = BinaryPrimitives.ReadUInt32BigEndian(body);
            body = body[4..];
        }
        return new TuyaMessage(seq, cmd, ret, body.ToArray());
    }

    // ---------------------------------------------------------------- payload helpers

    /// <summary>Removes a leading "3.x" + 12 byte version header if present.</summary>
    public static byte[] StripVersionHeader(byte[] payload)
    {
        if (payload.Length >= 15 && payload[0] == (byte)'3' && payload[1] == (byte)'.' && payload[2] is >= (byte)'0' and <= (byte)'9')
            return payload[15..];
        return payload;
    }

    /// <summary>Decrypts the payload of a 55AA frame for the given version (3.1/3.3: local key, 3.4: session key).</summary>
    public static byte[] DecryptPayload55AA(byte[] payload, TuyaVersion version, byte[] key)
    {
        if (payload.Length == 0) return payload;
        switch (version)
        {
            case TuyaVersion.V31:
                if (payload.Length > 19 && payload[0] == (byte)'3' && payload[1] == (byte)'.' && payload[2] == (byte)'1')
                    return EcbDecrypt(key, Convert.FromBase64String(Encoding.ASCII.GetString(payload, 19, payload.Length - 19)));
                return payload;
            case TuyaVersion.V33:
                payload = StripVersionHeader(payload);
                if (payload.Length % 16 == 0 && payload[0] != (byte)'{')
                    return EcbDecrypt(key, payload);
                return payload;
            case TuyaVersion.V34:
                if (payload.Length % 16 == 0 && payload[0] != (byte)'{')
                    payload = EcbDecrypt(key, payload);
                return StripVersionHeader(payload);
            default:
                throw new ArgumentOutOfRangeException(nameof(version));
        }
    }

    /// <summary>Encrypts a payload for a 55AA frame (versions 3.1/3.3/3.4).</summary>
    public static byte[] EncryptPayload55AA(TuyaCommand cmd, byte[] json, TuyaVersion version, byte[] key)
    {
        switch (version)
        {
            case TuyaVersion.V31:
                if (cmd != TuyaCommand.Control) return json;
                var b64 = Convert.ToBase64String(EcbEncrypt(key, json));
                var md5 = Convert.ToHexStringLower(MD5.HashData(Encoding.ASCII.GetBytes($"data={b64}||lpv=3.1||{Encoding.ASCII.GetString(key)}")));
                return Encoding.ASCII.GetBytes("3.1" + md5[8..24] + b64);
            case TuyaVersion.V33:
                var enc = EcbEncrypt(key, json);
                return HasNoVersionHeader(cmd) ? enc : [.. VersionHeader(version), .. enc];
            case TuyaVersion.V34:
                byte[] plain = HasNoVersionHeader(cmd) ? json : [.. VersionHeader(version), .. json];
                return EcbEncrypt(key, plain);
            default:
                throw new ArgumentOutOfRangeException(nameof(version));
        }
    }

    public static byte[] Xor(byte[] a, byte[] b)
    {
        var r = new byte[Math.Min(a.Length, b.Length)];
        for (int i = 0; i < r.Length; i++) r[i] = (byte)(a[i] ^ b[i]);
        return r;
    }

    /// <summary>Derives the 3.4/3.5 session key from both nonces.</summary>
    public static byte[] DeriveSessionKey(TuyaVersion version, byte[] localKey, byte[] localNonce, byte[] remoteNonce)
    {
        var x = Xor(localNonce, remoteNonce);
        if (version == TuyaVersion.V34)
            return EcbEncrypt(localKey, x, pad: false);
        var ct = new byte[16];
        var tag = new byte[16];
        using var gcm = new AesGcm(localKey, 16);
        gcm.Encrypt(localNonce.AsSpan(0, 12), x, ct, tag);
        return ct;
    }

    /// <summary>Extracts the JSON object from a (possibly noisy) decrypted payload.</summary>
    public static string? ExtractJson(byte[] payload)
    {
        int start = Array.IndexOf(payload, (byte)'{');
        int end = Array.LastIndexOf(payload, (byte)'}');
        return start >= 0 && end > start ? Encoding.UTF8.GetString(payload, start, end - start + 1) : null;
    }
}

/// <summary>Standard CRC-32 (IEEE 802.3).</summary>
public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }
}
