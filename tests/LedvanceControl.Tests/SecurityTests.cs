using System.Security.Cryptography;
using System.Text.Json;
using LedvanceControl.Core.Model;
using LedvanceControl.Core.Security;
using LedvanceControl.Core.Services;
using Xunit;

namespace LedvanceControl.Tests;

/// <summary>A master-key store kept in memory (stands in for the keyring / systemd-creds).</summary>
internal sealed class MemoryKeySource : IMasterKeySource
{
    public byte[]? Key;
    public string Name => "test key store";
    public bool IsAvailable() => true;
    public byte[]? Load() => Key;
    public void Store(byte[] key) => Key = key;
}

/// <summary>Scripted stand-in for secret-tool / systemd-creds.</summary>
internal sealed class FakeRunner(Func<string, IReadOnlyList<string>, string?, CommandResult> handler, params string[] programs) : ICommandRunner
{
    public readonly List<string> Calls = [];
    public bool Exists(string program) => programs.Contains(program);
    public CommandResult Run(string program, IReadOnlyList<string> args, string? stdin, TimeSpan? timeout = null)
    {
        Calls.Add(program + " " + string.Join(' ', args));
        return handler(program, args, stdin);
    }
}

public sealed class SecurityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "security-test-" + Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(_dir, "IoTSmartControl", "config.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static AppConfig WithSecrets() => new()
    {
        SmartThings = new SmartThingsSettings { ClientSecret = "st-secret-123", RefreshToken = "refresh-456" },
        Cloud = new CloudSettings { ClientSecret = "tuya-secret-789" },
        MatterStorageKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        Devices = [new DeviceConfig { Id = "d", Name = "Lamp", LocalKey = "0123456789abcdef" }],
    };

    private static readonly string[] Secrets = ["st-secret-123", "refresh-456", "tuya-secret-789", "0123456789abcdef"];

    [Fact]
    public void IpSortKey_OrdersNumerically()
    {
        string[] ips = ["192.168.1.100", "192.168.1.20", "fe80::1", "", "10.0.0.5:5540", "not an ip", "192.168.1.3"];
        var sorted = ips.OrderBy(SortKeys.Ip, StringComparer.Ordinal).ToArray();
        Assert.Equal(["10.0.0.5:5540", "192.168.1.3", "192.168.1.20", "192.168.1.100", "fe80::1", "", "not an ip"], sorted);
    }

    // ---------------------------------------------------------------- AES-GCM master-key protector

    [Fact]
    public void MasterKey_RoundTrip_TamperAndWrongKeyAreRejected()
    {
        var source = new MemoryKeySource();
        var p = new MasterKeyProtector(source);
        var stored = p.Protect("hello secret");
        Assert.StartsWith("aesgcm1:", stored);
        Assert.DoesNotContain("hello", stored);
        Assert.NotEqual(stored, p.Protect("hello secret")); // fresh nonce every time
        Assert.Equal("hello secret", p.Unprotect(stored));
        Assert.Equal(32, source.Key!.Length);

        var bytes = Convert.FromBase64String(stored["aesgcm1:".Length..]);
        bytes[^1] ^= 1;
        Assert.Null(p.Unprotect("aesgcm1:" + Convert.ToBase64String(bytes)));      // tampered
        var other = new MasterKeyProtector(new MemoryKeySource());
        Assert.Null(other.Unprotect(stored));                                         // other key
    }

    // ---------------------------------------------------------------- Linux key sources (scripted)

    [Fact]
    public void SecretService_CreatesTheKeyOnce_OverStdin()
    {
        string? kept = null;
        var runner = new FakeRunner((prog, args, stdin) => args[0] switch
        {
            "lookup" => kept == null ? new CommandResult(1, "", "") : new CommandResult(0, kept + "\n", ""),
            "store" => (kept = stdin) is not null ? new CommandResult(0, "", "") : new CommandResult(1, "", "no input"),
            _ => new CommandResult(2, "", "unexpected"),
        }, "secret-tool");
        var source = new SecretServiceKeySource(runner);
        Assert.True(source.IsAvailable());
        var p = new MasterKeyProtector(source);
        var stored = p.Protect("token");
        Assert.Equal("token", new MasterKeyProtector(source).Unprotect(stored)); // a new process finds the same key
        Assert.Single(runner.Calls, c => c.StartsWith("secret-tool store"));
        Assert.DoesNotContain(runner.Calls, c => c.Contains(kept!)); // the key never appears on a command line
    }

    [Fact]
    public void SecretService_WithoutAReachableKeyring_IsUnavailable()
    {
        var runner = new FakeRunner((_, _, _) => new CommandResult(1, "", "Cannot autolaunch D-Bus without X11 $DISPLAY"), "secret-tool");
        Assert.False(new SecretServiceKeySource(runner).IsAvailable());
        Assert.False(new SecretServiceKeySource(new FakeRunner((_, _, _) => new CommandResult(0, "", ""))).IsAvailable()); // not installed
    }

    [Fact]
    public void SystemdCreds_OldSystemd_IsUnavailable_NewOneStoresAKeyFile()
    {
        var credPath = Path.Combine(_dir, "master-key.cred");
        var old = new FakeRunner((_, _, _) => new CommandResult(1, "", "unrecognized option '--user'"), "systemd-creds");
        Assert.False(new SystemdCredsKeySource(old, credPath).IsAvailable());

        var store = new Dictionary<string, string>();
        var modern = new FakeRunner((_, args, stdin) =>
        {
            if (args[0] == "encrypt" && args[^1] == "-") return new CommandResult(0, "probe-ciphertext", "");
            if (args[0] == "encrypt") { Directory.CreateDirectory(_dir); File.WriteAllText(args[^1], "CRED"); store[args[^1]] = stdin!; return new CommandResult(0, "", ""); }
            if (args[0] == "decrypt") return store.TryGetValue(args[^2], out var v) ? new CommandResult(0, v, "") : new CommandResult(1, "", "bad");
            return new CommandResult(2, "", "");
        }, "systemd-creds");
        var source = new SystemdCredsKeySource(modern, credPath);
        Assert.True(source.IsAvailable());
        var p = new MasterKeyProtector(source);
        Assert.Equal("x", new MasterKeyProtector(source).Unprotect(p.Protect("x")));
        Assert.True(File.Exists(credPath));
    }

    // ---------------------------------------------------------------- config store

    [Fact]
    public void Config_WithASecretStore_HoldsNoPlaintextSecrets()
    {
        var store = new ConfigStore(ConfigPath, new MasterKeyProtector(new MemoryKeySource()));
        var cfg = WithSecrets();
        store.Save(cfg);
        var json = File.ReadAllText(ConfigPath);
        Assert.All(Secrets, s => Assert.DoesNotContain(s, json));
        Assert.DoesNotContain(cfg.MatterStorageKey!, json);
        Assert.False(store.SecretsNotSaved);

        var back = store.Load();
        Assert.Equal("refresh-456", back.SmartThings.RefreshToken);
        Assert.Equal("0123456789abcdef", back.Devices[0].LocalKey);
        Assert.Equal(cfg.MatterStorageKey, back.MatterStorageKey);
    }

    [Fact]
    public void Config_WithoutASecretStore_NeverWritesSecrets()
    {
        var store = new ConfigStore(ConfigPath, protector: null);
        var cfg = WithSecrets();
        store.Save(cfg);
        var json = File.ReadAllText(ConfigPath);
        Assert.All(Secrets, s => Assert.DoesNotContain(s, json));
        Assert.True(store.SecretsNotSaved);
        Assert.Equal("refresh-456", cfg.SmartThings.RefreshToken);   // still usable for this session
        Assert.Contains("not saved", store.ProtectionDescription);
    }

    [Fact]
    public void Config_LegacyPlaintext_IsEncryptedOnLoad()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(WithSecrets())); // e.g. written by an older Linux build
        var store = new ConfigStore(ConfigPath, new MasterKeyProtector(new MemoryKeySource()));
        var cfg = store.Load();
        Assert.Equal("st-secret-123", cfg.SmartThings.ClientSecret);
        var json = File.ReadAllText(ConfigPath);
        Assert.All(Secrets, s => Assert.DoesNotContain(s, json));
        Assert.Contains("aesgcm1:", json);
    }

    [Fact]
    public void Config_ValueFromAnotherStore_IsDroppedNotMisread()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, """{ "SmartThings": { "RefreshToken": "aesgcm1:AAAA" } }""");
        var cfg = new ConfigStore(ConfigPath, new MasterKeyProtector(new MemoryKeySource())).Load();
        Assert.Null(cfg.SmartThings.RefreshToken);
    }

    [Fact]
    public void Config_FolderAndFile_AreOwnerOnly()
    {
        var store = new ConfigStore(ConfigPath, new MasterKeyProtector(new MemoryKeySource()));
        store.Save(WithSecrets());
        var dir = Path.GetDirectoryName(ConfigPath)!;
        Assert.True(FilePermissions.IsOwnerOnly(dir));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(dir));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(ConfigPath));
        }
    }

    /// <summary>
    /// Regression: a config saved into an existing folder the app doesn't own (e.g. via LEDVANCE_CONFIG, or the
    /// temp folder) must not change that folder's permissions — only the file is restricted.
    /// </summary>
    [Fact]
    public void Config_InASharedFolder_LeavesTheFolderAlone()
    {
        var shared = Path.Combine(_dir, "shared");
        Directory.CreateDirectory(shared);
        var before = Describe(shared);
        var file = Path.Combine(shared, "config.json");
        new ConfigStore(file, new MasterKeyProtector(new MemoryKeySource())).Save(WithSecrets());
        Assert.Equal(before, Describe(shared));
        if (OperatingSystem.IsWindows())
        {
            var acl = new FileInfo(file).GetAccessControl();
            Assert.True(acl.AreAccessRulesProtected);          // the file itself is owner-only
        }
        else Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));

        static string Describe(string path) => OperatingSystem.IsWindows()
            ? new DirectoryInfo(path).GetAccessControl().GetSecurityDescriptorSddlForm(System.Security.AccessControl.AccessControlSections.Access)
            : File.GetUnixFileMode(path).ToString();
    }

    // ---------------------------------------------------------------- Matter storage key

    [Fact]
    public async Task MatterStorageKey_IsCreatedOnce_AndMatterIsOffWithoutASecretStore()
    {
        var node = MatterBridgeLookup();
        if (node == null) return;
        var opts = new LedvanceControl.Core.Local.MatterBridgeOptions(node, Path.Combine(_dir, "bridge.mjs"), Path.Combine(_dir, "matter"));

        var keys = new MemoryKeySource();
        string? first;
        await using (var m = new DeviceManager(new ConfigStore(ConfigPath, new MasterKeyProtector(keys)), opts))
        {
            Assert.NotNull(m.Matter);
            first = m.Matter!.Options.StorageKey;
            Assert.Equal(32, Convert.FromBase64String(first!).Length);
        }
        await using (var m = new DeviceManager(new ConfigStore(ConfigPath, new MasterKeyProtector(keys)), opts))
            Assert.Equal(first, m.Matter!.Options.StorageKey);           // the same key next time
        Assert.DoesNotContain(first!, File.ReadAllText(ConfigPath));   // and never in plain text

        await using var none = new DeviceManager(new ConfigStore(Path.Combine(_dir, "other", "config.json"), protector: null), opts);
        Assert.Null(none.Matter);
        Assert.Contains("secure secret store", none.MatterUnavailableReason);
    }

    // ---------------------------------------------------------------- real Linux stores (opt-in)

    /// <summary>Set by the Fedora test run; these tests use the machine's real keyring / systemd.</summary>
    private static bool LinuxIntegration => OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("LEDVANCE_LINUX_INTEGRATION") == "1";

    [Fact]
    public void Linux_RealSystemdCreds_KeepsTheKeyInAPrivateCredentialFile()
    {
        if (!LinuxIntegration) return;
        var credPath = Path.Combine(_dir, "IoTSmartControl", "master-key.cred");
        var source = new SystemdCredsKeySource(new ProcessCommandRunner(), credPath);
        Assert.True(source.IsAvailable(), "systemd-creds --user should work on Fedora 41+");
        var stored = new MasterKeyProtector(source).Protect("fedora secret");
        Assert.Equal("fedora secret", new MasterKeyProtector(source).Unprotect(stored)); // fresh instance decrypts via systemd
        Assert.True(File.Exists(credPath));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(credPath));
        Assert.DoesNotContain("fedora", File.ReadAllText(credPath));
    }

    [Fact]
    public void Linux_RealSecretService_KeepsTheKeyInTheKeyring()
    {
        if (!LinuxIntegration || Environment.GetEnvironmentVariable("LEDVANCE_TEST_KEYRING") != "1") return;
        var runner = new ProcessCommandRunner();
        var source = new SecretServiceKeySource(runner);
        Assert.True(source.IsAvailable());
        var stored = new MasterKeyProtector(source).Protect("keyring secret");
        Assert.Equal("keyring secret", new MasterKeyProtector(new SecretServiceKeySource(runner)).Unprotect(stored));
        var p = SecretProtection.CreateDefault(_dir);
        Assert.NotNull(p);
        Assert.Contains("keyring", p!.Name);                       // the keyring wins when it is running
    }

    [Fact]
    public void Linux_DefaultStore_IsSystemdCreds_WhenNoKeyringRuns()
    {
        if (!LinuxIntegration || Environment.GetEnvironmentVariable("LEDVANCE_TEST_KEYRING") == "1") return;
        // Cut this process (and the secret-tool it starts) off from the session keyring, so the fallback
        // is exercised and nothing lands in the user's real keyring.
        var bus = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
        Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", "unix:path=/nonexistent/bus");
        try
        {
            var p = SecretProtection.CreateDefault(Path.Combine(_dir, "cfg"));
            Assert.NotNull(p);
            Assert.Contains("systemd", p!.Name);
            var store = new ConfigStore(Path.Combine(_dir, "cfg", "config.json"));
            store.Save(WithSecrets());
            Assert.False(store.SecretsNotSaved);
            Assert.All(Secrets, s => Assert.DoesNotContain(s, File.ReadAllText(Path.Combine(_dir, "cfg", "config.json"))));
        }
        finally { Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", bus); }
    }

    private static string? MatterBridgeLookup() =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
        .Select(d => Path.Combine(d.Trim('"'), OperatingSystem.IsWindows() ? "node.exe" : "node")).FirstOrDefault(File.Exists) ?? "node";
}
