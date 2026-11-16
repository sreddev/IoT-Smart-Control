using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace LedvanceControl.Core.Security;

/// <summary>Raised when a secret must be stored but no secure store exists on this machine.</summary>
public sealed class SecretStoreUnavailableException(string message) : Exception(message);

/// <summary>
/// Encrypts secrets for storage on disk. Every protected value starts with the protector's
/// <see cref="Prefix"/>, so a config file can say how each value was protected.
/// </summary>
public interface ISecretProtector
{
    /// <summary>Human-readable name of where the protection comes from, e.g. "Windows DPAPI".</summary>
    string Name { get; }
    /// <summary>Tag at the start of protected values, e.g. "dpapi:".</summary>
    string Prefix { get; }
    string Protect(string plaintext);
    /// <summary>Returns null when the value can't be decrypted here (other user / machine / key).</summary>
    string? Unprotect(string stored);
}

/// <summary>
/// Picks the secret store for this platform:
/// Windows → DPAPI (current user); Linux → a random master key kept in the desktop keyring
/// (Secret Service via <c>secret-tool</c>), else in a <c>systemd-creds --user</c> credential.
/// When none is available the result is null and callers must not write secrets to disk.
/// </summary>
public static class SecretProtection
{
    public const string MasterKeyLabel = "IoT Smart Control master key";

    public static ISecretProtector? CreateDefault(string configDirectory, ICommandRunner? runner = null)
    {
        if (OperatingSystem.IsWindows()) return new DpapiProtector();
        runner ??= new ProcessCommandRunner();
        IMasterKeySource[] sources =
        [
            new SecretServiceKeySource(runner),
            new SystemdCredsKeySource(runner, Path.Combine(configDirectory, "master-key.cred")),
        ];
        // A store only counts once it has actually produced the master key: a keyring that D-Bus starts on
        // demand answers lookups fine but may need an unlock prompt to store — which never appears without
        // a display. Then the next store is tried.
        foreach (var source in sources)
        {
            if (!source.IsAvailable()) continue;
            var protector = new MasterKeyProtector(source);
            if (protector.TryInitialize()) return protector;
        }
        return null;
    }

    /// <summary>True when a desktop session could show a keyring unlock prompt.</summary>
    internal static bool HasDisplay =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")) ||
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));
}

// ============================================================================ Windows DPAPI

/// <summary>Windows DPAPI, current-user scope (the format older versions already wrote).</summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiProtector : ISecretProtector
{
    public string Name => "Windows DPAPI";
    public string Prefix => "dpapi:";

    public string Protect(string plaintext) =>
        Prefix + Convert.ToBase64String(Dpapi.Run(Encoding.UTF8.GetBytes(plaintext), protect: true));

    public string? Unprotect(string stored)
    {
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        try { return Encoding.UTF8.GetString(Dpapi.Run(Convert.FromBase64String(stored[Prefix.Length..]), protect: false)); }
        catch { return null; } // other user / machine
    }

    /// <summary>Minimal CryptProtectData / CryptUnprotectData wrapper.</summary>
    private static class Dpapi
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob { public int cbData; public IntPtr pbData; }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref DataBlob pDataIn, string? szDataDescr, IntPtr pOptionalEntropy,
            IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(ref DataBlob pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
            IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);

        private const int UiForbidden = 0x1;

        public static byte[] Run(byte[] data, bool protect)
        {
            var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                var input = new DataBlob { cbData = data.Length, pbData = handle.AddrOfPinnedObject() };
                var ok = protect
                    ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
                if (!ok) throw new CryptographicException(Marshal.GetLastWin32Error());
                try
                {
                    var result = new byte[output.cbData];
                    Marshal.Copy(output.pbData, result, 0, output.cbData);
                    return result;
                }
                finally { LocalFree(output.pbData); }
            }
            finally { handle.Free(); }
        }
    }
}

// ============================================================================ master key + AES-GCM

/// <summary>Somewhere a 256-bit master key can be kept outside the config file.</summary>
public interface IMasterKeySource
{
    string Name { get; }
    /// <summary>True when the store works on this machine (tool present, session reachable).</summary>
    bool IsAvailable();
    /// <summary>The stored key, or null when none was stored yet.</summary>
    byte[]? Load();
    void Store(byte[] key);
}

/// <summary>
/// AES-256-GCM with a random master key held by an <see cref="IMasterKeySource"/>.
/// Format: <c>aesgcm1:</c> + base64(nonce[12] ‖ tag[16] ‖ ciphertext).
/// </summary>
public sealed class MasterKeyProtector(IMasterKeySource source) : ISecretProtector
{
    private readonly object _gate = new();
    private byte[]? _key;

    public string Name => source.Name;
    public string Prefix => "aesgcm1:";

    private byte[] Key
    {
        get
        {
            lock (_gate)
            {
                if (_key != null) return _key;
                var key = source.Load();
                if (key == null)
                {
                    key = RandomNumberGenerator.GetBytes(32);
                    source.Store(key);
                    // read it back so a store that silently failed can't leave secrets undecryptable later
                    var check = source.Load();
                    if (check == null || !check.AsSpan().SequenceEqual(key))
                        throw new SecretStoreUnavailableException($"{source.Name} did not keep the master key.");
                }
                if (key.Length != 32) throw new SecretStoreUnavailableException($"{source.Name} returned a malformed master key.");
                return _key = key;
            }
        }
    }

    /// <summary>Loads or creates the master key now; false when the store can't provide one.</summary>
    public bool TryInitialize()
    {
        try { _ = Key; return true; }
        catch (SecretStoreUnavailableException) { return false; }
    }

    public string Protect(string plaintext) => Prefix + Convert.ToBase64String(Seal(Key, Encoding.UTF8.GetBytes(plaintext)));

    public string? Unprotect(string stored)
    {
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        try { return Encoding.UTF8.GetString(Open(Key, Convert.FromBase64String(stored[Prefix.Length..]))); }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException) { return null; }
    }

    public static byte[] Seal(byte[] key, byte[] plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plaintext.Length];
        using (var gcm = new AesGcm(key, 16)) gcm.Encrypt(nonce, plaintext, cipher, tag);
        return [.. nonce, .. tag, .. cipher];
    }

    public static byte[] Open(byte[] key, byte[] sealedData)
    {
        if (sealedData.Length < 28) throw new CryptographicException("Ciphertext too short.");
        var plain = new byte[sealedData.Length - 28];
        using (var gcm = new AesGcm(key, 16)) gcm.Decrypt(sealedData.AsSpan(0, 12), sealedData.AsSpan(28), sealedData.AsSpan(12, 16), plain);
        return plain;
    }
}

/// <summary>
/// The desktop keyring (GNOME Keyring / KWallet via the freedesktop Secret Service), through
/// <c>secret-tool</c> from libsecret. The key travels over stdin/stdout, never on a command line.
/// </summary>
public sealed class SecretServiceKeySource(ICommandRunner runner) : IMasterKeySource
{
    private static readonly string[] Attributes = ["application", "iot-smart-control", "key", "master-v1"];

    public string Name => "the desktop keyring (Secret Service)";

    /// <summary>With a display the user may need time to answer an unlock prompt; without one no prompt can appear.</summary>
    private static TimeSpan Wait => SecretProtection.HasDisplay ? TimeSpan.FromSeconds(60) : TimeSpan.FromSeconds(5);

    public bool IsAvailable()
    {
        if (!runner.Exists("secret-tool")) return false;
        var r = runner.Run("secret-tool", ["lookup", .. Attributes], null, Wait);
        // exit 0 = found; exit 1 with no error text = not stored yet; anything else = no reachable keyring
        return r.ExitCode == 0 || (r.ExitCode == 1 && string.IsNullOrWhiteSpace(r.StdErr));
    }

    public byte[]? Load()
    {
        var r = runner.Run("secret-tool", ["lookup", .. Attributes], null, Wait);
        if (r.ExitCode != 0) return null;
        try { return Convert.FromBase64String(r.StdOut.Trim()); }
        catch (FormatException) { return null; }
    }

    public void Store(byte[] key)
    {
        var r = runner.Run("secret-tool", ["store", $"--label={SecretProtection.MasterKeyLabel}", .. Attributes], Convert.ToBase64String(key), Wait);
        if (r.ExitCode != 0) throw new SecretStoreUnavailableException("The desktop keyring refused to store the key: " + r.StdErr.Trim());
    }
}

/// <summary>
/// A <c>systemd-creds --user</c> credential file (systemd 256+, e.g. Fedora 41+): encrypted with a
/// key only this user's systemd can use, and TPM2-bound where the machine has one.
/// </summary>
public sealed class SystemdCredsKeySource(ICommandRunner runner, string credentialPath) : IMasterKeySource
{
    private const string CredentialName = "iot-smart-control-master";

    public string Name => "a systemd user credential";

    public bool IsAvailable()
    {
        if (!runner.Exists("systemd-creds")) return false;
        if (File.Exists(credentialPath)) return true;
        // Probe: older systemd (< 256) has no --user and fails here.
        var r = runner.Run("systemd-creds", ["encrypt", "--user", $"--name={CredentialName}-probe", "-", "-"], "probe");
        return r.ExitCode == 0;
    }

    public byte[]? Load()
    {
        if (!File.Exists(credentialPath)) return null;
        var r = runner.Run("systemd-creds", ["decrypt", "--user", $"--name={CredentialName}", credentialPath, "-"], null);
        if (r.ExitCode != 0) return null;
        try { return Convert.FromBase64String(r.StdOut.Trim()); }
        catch (FormatException) { return null; }
    }

    public void Store(byte[] key)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(credentialPath)!);
        var r = runner.Run("systemd-creds", ["encrypt", "--user", $"--name={CredentialName}", "-", credentialPath], Convert.ToBase64String(key));
        if (r.ExitCode != 0) throw new SecretStoreUnavailableException("systemd-creds could not store the key: " + r.StdErr.Trim());
        FilePermissions.RestrictFile(credentialPath);
    }
}

// ============================================================================ running helper tools

public sealed record CommandResult(int ExitCode, string StdOut, string StdErr);

/// <summary>Runs a helper program (abstracted so tests can fake <c>secret-tool</c> / <c>systemd-creds</c>).</summary>
public interface ICommandRunner
{
    bool Exists(string program);
    CommandResult Run(string program, IReadOnlyList<string> args, string? stdin, TimeSpan? timeout = null);
}

public sealed class ProcessCommandRunner : ICommandRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public bool Exists(string program) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Any(dir => File.Exists(Path.Combine(dir, program)));

    public CommandResult Run(string program, IReadOnlyList<string> args, string? stdin, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            if (stdin != null) p.StandardInput.Write(stdin);
            p.StandardInput.Close();
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeout ?? DefaultTimeout))
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return new CommandResult(-1, "", "timed out");
            }
            return new CommandResult(p.ExitCode, stdout.Result, stderr.Result);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new CommandResult(-1, "", ex.Message);
        }
    }
}
