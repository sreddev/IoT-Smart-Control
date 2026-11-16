using System.Text;
using System.Text.Json;
using LedvanceControl.Core.Security;

namespace LedvanceControl.Core.Model;

/// <summary>
/// Loads/saves <see cref="AppConfig"/> as JSON (Windows: %APPDATA%\IoTSmartControl, Linux:
/// ~/.config/IoTSmartControl). Secrets — cloud and SmartThings client secrets, the SmartThings refresh
/// token, device local keys and the Matter storage key — are encrypted with the platform's secret store
/// (<see cref="SecretProtection"/>). Secrets are never written in plain text: without a secure store
/// they are left out of the file (<see cref="SecretsNotSaved"/>). The folder is owner-only.
/// </summary>
public sealed class ConfigStore
{
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Tags of every protection format this or an older version may have written.</summary>
    private static readonly string[] KnownPrefixes = ["dpapi:", "aesgcm1:"];

    private bool _directorySecured;
    private readonly bool _ownsDirectory;

    /// <summary>Uses the platform's default secret store.</summary>
    public ConfigStore(string? path = null) : this(path, protector: null, useDefaultProtector: true) { }

    /// <summary>Uses the given secret store; null means none is available (secrets are not saved).</summary>
    public ConfigStore(string? path, ISecretProtector? protector) : this(path, protector, useDefaultProtector: false) { }

    private ConfigStore(string? path, ISecretProtector? protector, bool useDefaultProtector)
    {
        // LEDVANCE_CONFIG allows a portable / alternative configuration file
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var defaultPath = System.IO.Path.Combine(appData, "IoTSmartControl", "config.json");
        Path = path ?? Environment.GetEnvironmentVariable("LEDVANCE_CONFIG") ?? defaultPath;
        // Lock down a folder only when it belongs to the app: its default folder, or one it creates now.
        // A config placed in an existing folder (e.g. via LEDVANCE_CONFIG) only gets the file restricted.
        _ownsDirectory = string.Equals(System.IO.Path.GetFullPath(Path), System.IO.Path.GetFullPath(defaultPath), StringComparison.OrdinalIgnoreCase)
                         || !System.IO.Directory.Exists(Directory);
        Protector = useDefaultProtector ? SecretProtection.CreateDefault(Directory) : protector;

        // Migrate a config written by the previous app name (%APPDATA%\LedvanceControl) so an
        // existing cloud/SmartThings connection carries over (DPAPI secrets are user-scoped).
        if (path == null && !File.Exists(Path))
        {
            var legacy = System.IO.Path.Combine(appData, "LedvanceControl", "config.json");
            if (File.Exists(legacy))
            {
                SecureDirectory();
                File.Copy(legacy, Path);
                FilePermissions.RestrictFile(Path);
            }
        }
    }

    public string Path { get; }
    public string Directory => System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!;

    /// <summary>The secret store in use, or null when this machine has none.</summary>
    public ISecretProtector? Protector { get; }

    /// <summary>"Windows DPAPI", "the desktop keyring (Secret Service)", … or a warning.</summary>
    public string ProtectionDescription => Protector?.Name ?? "no secure store available — secrets are not saved";

    /// <summary>True after a save had to leave secrets out (no secure store on this machine).</summary>
    public bool SecretsNotSaved { get; private set; }

    public AppConfig Load()
    {
        if (!File.Exists(Path)) return new AppConfig();
        SecureDirectory();
        var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(Path), JsonOptions) ?? new AppConfig();
        var plaintextFound = false;
        string? Open(string? s)
        {
            var (value, wasPlain) = Unprotect(s);
            plaintextFound |= wasPlain;
            return value;
        }
        cfg.Cloud.ClientSecret = Open(cfg.Cloud.ClientSecret);
        cfg.SmartThings.ClientSecret = Open(cfg.SmartThings.ClientSecret);
        cfg.SmartThings.RefreshToken = Open(cfg.SmartThings.RefreshToken);
        cfg.MatterStorageKey = Open(cfg.MatterStorageKey);
        foreach (var d in cfg.Devices) d.LocalKey = Open(d.LocalKey);

        // A plain-text secret (e.g. written by an older Linux build): re-save so it gets encrypted,
        // or dropped from disk when there is no secure store.
        if (plaintextFound) Save(cfg);
        return cfg;
    }

    public void Save(AppConfig cfg)
    {
        SecureDirectory();
        // serialize a protected copy so the in-memory config keeps plaintext secrets
        var copy = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(cfg, JsonOptions), JsonOptions)!;
        SecretsNotSaved = false;
        copy.Cloud.ClientSecret = Protect(copy.Cloud.ClientSecret);
        copy.SmartThings.ClientSecret = Protect(copy.SmartThings.ClientSecret);
        copy.SmartThings.RefreshToken = Protect(copy.SmartThings.RefreshToken);
        copy.MatterStorageKey = Protect(copy.MatterStorageKey);
        foreach (var d in copy.Devices) d.LocalKey = Protect(d.LocalKey);

        var tmp = Path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = FilePermissions.PrivateFileMode; // 0600 from the start
        using (var writer = new StreamWriter(tmp, new UTF8Encoding(false), options))
            writer.Write(JsonSerializer.Serialize(copy, JsonOptions));
        File.Move(tmp, Path, overwrite: true);
        try { FilePermissions.RestrictFile(Path, ownAcl: !_ownsDirectory); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            System.Diagnostics.Debug.WriteLine("Could not restrict " + Path + ": " + ex.Message);
        }
    }

    private void SecureDirectory()
    {
        if (_directorySecured) return;
        if (!_ownsDirectory)
        {
            System.IO.Directory.CreateDirectory(Directory); // an existing, shared folder: leave its permissions alone
            _directorySecured = true;
            return;
        }
        try
        {
            FilePermissions.SecureDirectory(Directory);
            _directorySecured = true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException
                                       or PlatformNotSupportedException or System.Security.Principal.IdentityNotMappedException)
        {
            // e.g. a directory on a filesystem without ACLs / owned by someone else: keep working
            System.Diagnostics.Debug.WriteLine("Could not restrict permissions on " + Directory + ": " + ex.Message);
        }
    }

    private string? Protect(string? s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        if (Protector == null)
        {
            SecretsNotSaved = true;
            return null;
        }
        try { return Protector.Protect(s); }
        catch (SecretStoreUnavailableException)
        {
            SecretsNotSaved = true;
            return null;
        }
    }

    /// <returns>The plaintext (null when it can't be decrypted here) and whether the stored value was plain text.</returns>
    private (string? Value, bool WasPlain) Unprotect(string? s)
    {
        if (string.IsNullOrEmpty(s)) return (s, false);
        if (Protector != null && s.StartsWith(Protector.Prefix, StringComparison.Ordinal))
        {
            try { return (Protector.Unprotect(s), false); }
            catch (SecretStoreUnavailableException) { return (null, false); }
        }
        if (KnownPrefixes.Any(p => s.StartsWith(p, StringComparison.Ordinal)))
            return (null, false); // protected by a store that isn't available here (other user / machine / OS)
        return (s, true);         // legacy plain text
    }
}
