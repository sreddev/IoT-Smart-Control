using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace LedvanceControl.Core.Model;

/// <summary>
/// Loads/saves <see cref="AppConfig"/> as JSON under %APPDATA%\LedvanceControl.
/// Secrets (cloud client secret, device local keys) are encrypted with Windows DPAPI for the current user.
/// </summary>
public sealed class ConfigStore
{
    private const string ProtectedPrefix = "dpapi:";
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public ConfigStore(string? path = null)
    {
        // LEDVANCE_CONFIG allows a portable / alternative configuration file
        Path = path ?? Environment.GetEnvironmentVariable("LEDVANCE_CONFIG") ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LedvanceControl", "config.json");
    }

    public string Path { get; }

    public AppConfig Load()
    {
        if (!File.Exists(Path)) return new AppConfig();
        var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(Path), JsonOptions) ?? new AppConfig();
        cfg.Cloud.ClientSecret = Unprotect(cfg.Cloud.ClientSecret);
        cfg.SmartThings.ClientSecret = Unprotect(cfg.SmartThings.ClientSecret);
        cfg.SmartThings.RefreshToken = Unprotect(cfg.SmartThings.RefreshToken);
        foreach (var d in cfg.Devices) d.LocalKey = Unprotect(d.LocalKey);
        return cfg;
    }

    public void Save(AppConfig cfg)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        // serialize a protected copy so the in-memory config keeps plaintext secrets
        var copy = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(cfg, JsonOptions), JsonOptions)!;
        copy.Cloud.ClientSecret = Protect(copy.Cloud.ClientSecret);
        copy.SmartThings.ClientSecret = Protect(copy.SmartThings.ClientSecret);
        copy.SmartThings.RefreshToken = Protect(copy.SmartThings.RefreshToken);
        foreach (var d in copy.Devices) d.LocalKey = Protect(d.LocalKey);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(copy, JsonOptions));
        File.Move(tmp, Path, overwrite: true);
    }

    private static string? Protect(string? s)
    {
        if (string.IsNullOrEmpty(s) || !OperatingSystem.IsWindows()) return s;
        return ProtectedPrefix + Convert.ToBase64String(Dpapi.Protect(Encoding.UTF8.GetBytes(s)));
    }

    private static string? Unprotect(string? s)
    {
        if (s == null || !s.StartsWith(ProtectedPrefix, StringComparison.Ordinal)) return s;
        try
        {
            return Encoding.UTF8.GetString(Dpapi.Unprotect(Convert.FromBase64String(s[ProtectedPrefix.Length..])));
        }
        catch
        {
            return null; // config copied from another user/machine
        }
    }

    /// <summary>Minimal DPAPI (CryptProtectData) wrapper, current-user scope.</summary>
    private static class Dpapi
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int cbData;
            public IntPtr pbData;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref DataBlob pDataIn, string? szDataDescr, IntPtr pOptionalEntropy,
            IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(ref DataBlob pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
            IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);

        private const int UiForbidden = 0x1;

        public static byte[] Protect(byte[] data) => Run(data, protect: true);
        public static byte[] Unprotect(byte[] data) => Run(data, protect: false);

        private static byte[] Run(byte[] data, bool protect)
        {
            var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                var input = new DataBlob { cbData = data.Length, pbData = handle.AddrOfPinnedObject() };
                bool ok = protect
                    ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
                if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    var result = new byte[output.cbData];
                    Marshal.Copy(output.pbData, result, 0, output.cbData);
                    return result;
                }
                finally
                {
                    LocalFree(output.pbData);
                }
            }
            finally
            {
                handle.Free();
            }
        }
    }
}
