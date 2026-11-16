using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using LedvanceControl.Core.Model;

namespace LedvanceControl.Core.Backup;

/// <summary>Which parts of the configuration a backup file carries.</summary>
[Flags]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BackupParts
{
    None = 0,
    /// <summary>Houses, rooms and devices (names, rooms, IPs, schemas).</summary>
    Devices = 1,
    /// <summary>Groups, local scenes and schedules.</summary>
    Automations = 2,
    /// <summary>Theme, start minimized, close to tray, current house, cloud account ids.</summary>
    Preferences = 4,
    /// <summary>Local keys, client secrets and the SmartThings sign-in. Only ever written encrypted.</summary>
    Secrets = 8,
    All = Devices | Automations | Preferences | Secrets,
}

/// <summary>How an imported backup is combined with the settings already on this PC.</summary>
public enum ImportMode
{
    /// <summary>Add new items, update the ones with the same id, keep everything else.</summary>
    Merge,
    /// <summary>For every part the file carries, use only what is in the file.</summary>
    Replace,
}

/// <summary>Counts shown before an import (readable without the password).</summary>
public sealed record BackupSummary(int Houses, int Rooms, int Devices, int Groups, int Scenes, int Schedules)
{
    public override string ToString()
    {
        var parts = new List<string>();
        void Add(int n, string one, string many) { if (n > 0) parts.Add($"{n} {(n == 1 ? one : many)}"); }
        Add(Houses, "house", "houses");
        Add(Rooms, "room", "rooms");
        Add(Devices, "device", "devices");
        Add(Groups, "group", "groups");
        Add(Scenes, "scene", "scenes");
        Add(Schedules, "schedule", "schedules");
        return parts.Count == 0 ? "no houses or devices" : string.Join(", ", parts);
    }
}

/// <summary>The readable header of a backup file: what it is, when and where it was made, what it holds.</summary>
public sealed class BackupHeader
{
    public string Format { get; init; } = SettingsBackup.FormatId;
    public int Version { get; init; } = SettingsBackup.FormatVersion;
    public DateTimeOffset Created { get; init; }
    public string? AppVersion { get; init; }
    public string? Computer { get; init; }
    public BackupParts Parts { get; init; }
    public BackupSummary Summary { get; init; } = new(0, 0, 0, 0, 0, 0);
    /// <summary>True when the content is encrypted with a password (always the case when it holds secrets).</summary>
    public bool Encrypted { get; init; }
}

/// <summary>A backup file read from disk: its header, and the content once unlocked.</summary>
public sealed class BackupFile
{
    internal BackupFile(BackupHeader header, JsonObject envelope)
    {
        Header = header;
        Envelope = envelope;
    }

    public BackupHeader Header { get; }
    internal JsonObject Envelope { get; }
}

/// <summary>Raised for anything the user can fix: a wrong password, a damaged or foreign file.</summary>
public sealed class BackupException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Export / import of the app's settings to a single <c>.iscbackup</c> file.
/// <para>
/// File layout (JSON): a readable <b>header</b> (format, version, date, computer, parts, counts) and the
/// <b>content</b>, an <see cref="AppConfig"/>. Without a password the content is stored as plain JSON and
/// every secret is removed first. With a password the content is encrypted with AES-256-GCM; the key is
/// derived from the password with PBKDF2-HMAC-SHA256 (600 000 iterations, random 16-byte salt) and the
/// header is bound in as associated data, so editing the header breaks decryption just like editing
/// the ciphertext does.
/// </para>
/// <para>
/// Never exported: the Matter storage key. It protects this PC's own Matter controller identity, which
/// stays on this PC (paired Matter bulbs are paired again on another PC).
/// </para>
/// </summary>
public static class SettingsBackup
{
    public const string FormatId = "iot-smart-control-backup";
    public const int FormatVersion = 1;
    public const string FileExtension = ".iscbackup";
    /// <summary>PBKDF2 work factor (OWASP 2023 guidance for PBKDF2-HMAC-SHA256).</summary>
    public const int Pbkdf2Iterations = 600_000;
    public const int MinPasswordLength = 8;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    // ---------------------------------------------------------------- export

    /// <summary>
    /// Builds a backup of <paramref name="config"/>. <paramref name="password"/> is required when
    /// <paramref name="parts"/> includes <see cref="BackupParts.Secrets"/>; it may also be given without
    /// secrets to encrypt the rest.
    /// </summary>
    public static byte[] Export(AppConfig config, BackupParts parts, string? password, string? appVersion = null, DateTimeOffset? now = null)
    {
        if (parts.HasFlag(BackupParts.Secrets) && string.IsNullOrEmpty(password))
            throw new BackupException("Keys and credentials are only exported with a backup password.");
        if (!string.IsNullOrEmpty(password) && password.Length < MinPasswordLength)
            throw new BackupException($"The backup password needs at least {MinPasswordLength} characters.");

        var content = Select(Clone(config), parts);
        var header = new BackupHeader
        {
            Created = now ?? DateTimeOffset.Now,
            AppVersion = appVersion,
            Computer = Environment.MachineName,
            Parts = parts,
            Summary = Summarize(content, parts),
            Encrypted = !string.IsNullOrEmpty(password),
        };

        var envelope = new JsonObject { ["header"] = JsonSerializer.SerializeToNode(header, Json) };
        var contentJson = JsonSerializer.SerializeToUtf8Bytes(content, Json);
        if (string.IsNullOrEmpty(password))
        {
            envelope["content"] = JsonNode.Parse(contentJson);
        }
        else
        {
            var salt = RandomNumberGenerator.GetBytes(16);
            var nonce = RandomNumberGenerator.GetBytes(12);
            var key = DeriveKey(password, salt, Pbkdf2Iterations);
            var cipher = new byte[contentJson.Length];
            var tag = new byte[16];
            using (var aes = new AesGcm(key, 16))
                aes.Encrypt(nonce, contentJson, cipher, tag, HeaderAad(envelope["header"]!));
            CryptographicOperations.ZeroMemory(key);
            envelope["encryption"] = new JsonObject
            {
                ["cipher"] = "AES-256-GCM",
                ["kdf"] = "PBKDF2-HMAC-SHA256",
                ["iterations"] = Pbkdf2Iterations,
                ["salt"] = Convert.ToBase64String(salt),
                ["nonce"] = Convert.ToBase64String(nonce),
                ["tag"] = Convert.ToBase64String(tag),
            };
            envelope["content"] = Convert.ToBase64String(cipher);
        }
        return Encoding.UTF8.GetBytes(envelope.ToJsonString(Json));
    }

    // ---------------------------------------------------------------- import

    /// <summary>Reads the header (no password needed) so the import dialog can show what the file holds.</summary>
    public static BackupFile Read(byte[] data)
    {
        JsonObject envelope;
        try
        {
            envelope = JsonNode.Parse(data)?.AsObject() ?? throw new JsonException();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            throw new BackupException("This is not an IoT Smart Control backup file.", ex);
        }
        BackupHeader? header;
        try
        {
            header = envelope["header"]?.Deserialize<BackupHeader>(Json);
        }
        catch (JsonException ex)
        {
            throw new BackupException("The backup file is damaged (unreadable header).", ex);
        }
        if (header == null || header.Format != FormatId)
            throw new BackupException("This is not an IoT Smart Control backup file.");
        if (header.Version > FormatVersion)
            throw new BackupException("This backup was made by a newer version of the app. Update the app, then import it.");
        return new BackupFile(header, envelope);
    }

    /// <summary>Unlocks the content. <paramref name="password"/> is needed only for encrypted files.</summary>
    public static AppConfig Open(BackupFile file, string? password)
    {
        byte[] contentJson;
        if (!file.Header.Encrypted)
        {
            contentJson = Encoding.UTF8.GetBytes(file.Envelope["content"]?.ToJsonString() ?? "{}");
        }
        else
        {
            if (string.IsNullOrEmpty(password)) throw new BackupException("This backup is protected. Enter its password.");
            var enc = file.Envelope["encryption"]?.AsObject() ?? throw new BackupException("The backup file is damaged (no encryption details).");
            try
            {
                var salt = Convert.FromBase64String(enc["salt"]!.GetValue<string>());
                var nonce = Convert.FromBase64String(enc["nonce"]!.GetValue<string>());
                var tag = Convert.FromBase64String(enc["tag"]!.GetValue<string>());
                var iterations = enc["iterations"]!.GetValue<int>();
                var cipher = Convert.FromBase64String(file.Envelope["content"]!.GetValue<string>());
                if (iterations < 100_000) throw new BackupException("The backup file is damaged (weak key settings).");
                var key = DeriveKey(password, salt, iterations);
                contentJson = new byte[cipher.Length];
                try
                {
                    using var aes = new AesGcm(key, 16);
                    aes.Decrypt(nonce, cipher, tag, contentJson, HeaderAad(file.Envelope["header"]!));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(key);
                }
            }
            catch (AuthenticationTagMismatchException ex)
            {
                throw new BackupException("Wrong password, or the file was changed after it was exported.", ex);
            }
            catch (Exception ex) when (ex is FormatException or NullReferenceException or InvalidOperationException or ArgumentException or CryptographicException)
            {
                throw new BackupException("The backup file is damaged.", ex);
            }
        }
        try
        {
            return JsonSerializer.Deserialize<AppConfig>(contentJson, Json) ?? new AppConfig();
        }
        catch (JsonException ex)
        {
            throw new BackupException("The backup file is damaged (unreadable content).", ex);
        }
    }

    /// <summary>
    /// Combines <paramref name="incoming"/> (the backup's content, carrying <paramref name="parts"/>) with
    /// <paramref name="current"/> and returns the configuration to save. Neither input is modified.
    /// <list type="bullet">
    /// <item>Parts the file doesn't carry are left exactly as they are.</item>
    /// <item>A secret the file doesn't carry (exported without keys) is kept from this PC when the same
    /// device / account exists here, so a key-less backup never wipes working keys.</item>
    /// <item>This PC's Matter storage key always stays.</item>
    /// </list>
    /// </summary>
    public static AppConfig Merge(AppConfig current, AppConfig incoming, BackupParts parts, ImportMode mode)
    {
        var result = Clone(current);
        var file = Clone(incoming);

        if (parts.HasFlag(BackupParts.Devices))
        {
            if (mode == ImportMode.Replace)
            {
                result.Houses = file.Houses;
                result.Devices = file.Devices;
            }
            else
            {
                foreach (var h in file.Houses)
                {
                    var mine = result.Houses.FirstOrDefault(x => x.Id == h.Id);
                    if (mine == null) { result.Houses.Add(h); continue; }
                    // the file's house wins, but rooms only created here are kept
                    h.Rooms = h.Rooms.Union(mine.Rooms, StringComparer.OrdinalIgnoreCase).ToList();
                    result.Houses[result.Houses.IndexOf(mine)] = h;
                }
                Upsert(result.Devices, file.Devices, d => d.Id);
            }
            // keep working local keys the file doesn't carry
            foreach (var d in result.Devices.Where(d => string.IsNullOrEmpty(d.LocalKey)))
                d.LocalKey = current.Devices.FirstOrDefault(c => c.Id == d.Id)?.LocalKey;
        }

        if (parts.HasFlag(BackupParts.Automations))
        {
            if (mode == ImportMode.Replace)
            {
                result.Groups = file.Groups;
                result.Scenes = file.Scenes;
                result.Schedules = file.Schedules;
            }
            else
            {
                Upsert(result.Groups, file.Groups, g => g.Id);
                Upsert(result.Scenes, file.Scenes, s => s.Id);
                Upsert(result.Schedules, file.Schedules, s => s.Id);
            }
        }

        if (parts.HasFlag(BackupParts.Preferences))
        {
            result.Theme = file.Theme;
            result.StartMinimized = file.StartMinimized;
            result.CloseToTray = file.CloseToTray;
            if (file.CurrentHouseId != null && result.Houses.Any(h => h.Id == file.CurrentHouseId)) result.CurrentHouseId = file.CurrentHouseId;
            result.Cloud = MergeCloud(current.Cloud, file.Cloud);
            result.SmartThings = MergeSmartThings(current.SmartThings, file.SmartThings);
        }

        if (result.CurrentHouseId != null && result.Houses.All(h => h.Id != result.CurrentHouseId)) result.CurrentHouseId = result.Houses.FirstOrDefault()?.Id;
        result.MatterStorageKey = current.MatterStorageKey;
        return result;
    }

    // ---------------------------------------------------------------- safety copies

    /// <summary>
    /// Copies the saved configuration file (secrets in it stay protected for this PC/user) into
    /// <c>backups/before-import-*.json</c> next to it, keeping the newest <paramref name="keep"/>.
    /// Returns the copy's path, or null when there is nothing saved yet.
    /// </summary>
    public static string? SaveSafetyCopy(ConfigStore store, int keep = 10, DateTimeOffset? now = null)
    {
        if (!File.Exists(store.Path)) return null;
        var dir = SafetyDirectory(store);
        Security.FilePermissions.SecureDirectory(dir);
        var path = Path.Combine(dir, $"before-import-{(now ?? DateTimeOffset.Now):yyyyMMdd-HHmmss-fff}.json");
        File.Copy(store.Path, path, overwrite: true);
        Security.FilePermissions.RestrictFile(path);
        foreach (var old in SafetyCopies(store).Skip(keep)) TryDelete(old);
        return path;
    }

    /// <summary>The folder holding the automatic pre-import copies.</summary>
    public static string SafetyDirectory(ConfigStore store) => Path.Combine(store.Directory, "backups");

    /// <summary>Automatic pre-import copies, newest first.</summary>
    public static IReadOnlyList<string> SafetyCopies(ConfigStore store)
    {
        var dir = SafetyDirectory(store);
        return Directory.Exists(dir)
            ? Directory.GetFiles(dir, "before-import-*.json").OrderByDescending(Path.GetFileName, StringComparer.Ordinal).ToList()
            : [];
    }

    /// <summary>Puts a safety copy back as the configuration (undo of the last import).</summary>
    public static void RestoreSafetyCopy(ConfigStore store, string copy)
    {
        var tmp = store.Path + ".restore";
        File.Copy(copy, tmp, overwrite: true);
        File.Move(tmp, store.Path, overwrite: true);
        Security.FilePermissions.RestrictFile(store.Path);
        TryDelete(copy); // it has been used up; the next import makes a new one
    }

    /// <summary>Suggested file name for an export, e.g. <c>iot-smart-control-2026-11-16.iscbackup</c>.</summary>
    public static string SuggestedFileName(DateTimeOffset? now = null) => $"iot-smart-control-{(now ?? DateTimeOffset.Now):yyyy-MM-dd}{FileExtension}";

    // ---------------------------------------------------------------- helpers

    /// <summary>Keeps only the chosen parts; removes every secret unless <see cref="BackupParts.Secrets"/> is chosen.</summary>
    private static AppConfig Select(AppConfig c, BackupParts parts)
    {
        if (!parts.HasFlag(BackupParts.Devices)) { c.Houses = []; c.Devices = []; }
        if (!parts.HasFlag(BackupParts.Automations)) { c.Groups = []; c.Scenes = []; c.Schedules = []; }
        if (!parts.HasFlag(BackupParts.Preferences))
        {
            var fresh = new AppConfig();
            c.Theme = fresh.Theme; c.StartMinimized = fresh.StartMinimized; c.CloseToTray = fresh.CloseToTray;
            c.CurrentHouseId = null; c.Cloud = new CloudSettings(); c.SmartThings = new SmartThingsSettings();
        }
        if (!parts.HasFlag(BackupParts.Secrets))
        {
            foreach (var d in c.Devices) d.LocalKey = null;
            c.Cloud.ClientSecret = null;
            c.SmartThings.ClientSecret = null;
            c.SmartThings.RefreshToken = null;
        }
        c.MatterStorageKey = null; // never leaves this PC
        c.HomeName = null; c.HomeNameEdited = null; c.Rooms = null; // legacy fields
        return c;
    }

    private static BackupSummary Summarize(AppConfig c, BackupParts parts) => new(
        c.Houses.Count, c.Houses.Sum(h => h.Rooms.Count), c.Devices.Count, c.Groups.Count, c.Scenes.Count, c.Schedules.Count);

    private static CloudSettings MergeCloud(CloudSettings mine, CloudSettings file) => new()
    {
        Region = file.Region,
        ClientId = file.ClientId ?? mine.ClientId,
        UserId = file.UserId ?? mine.UserId,
        ClientSecret = file.ClientSecret ?? (file.ClientId == null || file.ClientId == mine.ClientId ? mine.ClientSecret : null),
    };

    private static SmartThingsSettings MergeSmartThings(SmartThingsSettings mine, SmartThingsSettings file)
    {
        var sameApp = file.ClientId == null || file.ClientId == mine.ClientId;
        return new SmartThingsSettings
        {
            ClientId = file.ClientId ?? mine.ClientId,
            RedirectUri = file.RedirectUri,
            ClientSecret = file.ClientSecret ?? (sameApp ? mine.ClientSecret : null),
            RefreshToken = file.RefreshToken ?? (sameApp ? mine.RefreshToken : null),
        };
    }

    private static void Upsert<T>(List<T> target, IEnumerable<T> items, Func<T, string> id)
    {
        foreach (var item in items)
        {
            var i = target.FindIndex(x => id(x) == id(item));
            if (i >= 0) target[i] = item; else target.Add(item);
        }
    }

    private static AppConfig Clone(AppConfig c) =>
        JsonSerializer.Deserialize<AppConfig>(JsonSerializer.SerializeToUtf8Bytes(c, Json), Json)!;

    private static byte[] DeriveKey(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);

    /// <summary>The header in a fixed text form, authenticated with the content.</summary>
    private static byte[] HeaderAad(JsonNode header) => Encoding.UTF8.GetBytes(FormatId + "\n" + header.ToJsonString());

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
