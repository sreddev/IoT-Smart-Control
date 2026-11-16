using System.Text;
using System.Text.Json.Nodes;
using LedvanceControl.Core.Backup;
using LedvanceControl.Core.Model;
using Xunit;

namespace LedvanceControl.Tests;

public sealed class BackupTests : IDisposable
{
    private const string Password = "correct horse battery";
    private const string LocalKey = "0123456789abcdef";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "backup-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static AppConfig Sample()
    {
        var house = new HouseConfig { Id = "h1", Name = "Home", Rooms = ["Kitchen", "Hall"] };
        return new AppConfig
        {
            Theme = "Light",
            StartMinimized = false,
            CurrentHouseId = "h1",
            MatterStorageKey = "matter-key-never-exported",
            Cloud = new CloudSettings { Region = "eu", ClientId = "tuya-id", ClientSecret = "tuya-secret" },
            SmartThings = new SmartThingsSettings { ClientId = "st-id", ClientSecret = "st-secret", RefreshToken = "st-refresh" },
            Houses = [house],
            Devices =
            [
                new DeviceConfig { Id = "plug", Name = "Desk plug", HouseId = "h1", Room = "Kitchen", Ip = "192.0.2.10", LocalKey = LocalKey, Version = "3.4" },
                new DeviceConfig { Id = "bulb", Name = "E27", HouseId = "h1", MatterNodeId = "1" },
            ],
            Groups = [new GroupConfig { Id = "g1", HouseId = "h1", Name = "All", DeviceIds = ["plug", "bulb"] }],
            Scenes = [new LocalScene { Id = "s1", Name = "Evening" }],
            Schedules = [new ScheduleEntry { Id = "t1", Name = "Morning", TargetType = ScheduleTarget.Device, TargetId = "plug" }],
        };
    }

    // ---------------------------------------------------------------- export / open

    [Fact]
    public void Encrypted_RoundTrip_KeepsEverything_AndHidesSecretsInTheFile()
    {
        var bytes = SettingsBackup.Export(Sample(), BackupParts.All, Password, "2.5.0");
        var text = Encoding.UTF8.GetString(bytes);
        foreach (var secret in new[] { LocalKey, "tuya-secret", "st-secret", "st-refresh", "matter-key-never-exported", "Desk plug" })
            Assert.DoesNotContain(secret, text); // the whole content is encrypted, names included

        var file = SettingsBackup.Read(bytes);
        Assert.True(file.Header.Encrypted);
        Assert.Equal(BackupParts.All, file.Header.Parts);
        Assert.Equal(new BackupSummary(1, 2, 2, 1, 1, 1), file.Header.Summary);
        Assert.Equal("2.5.0", file.Header.AppVersion);

        var back = SettingsBackup.Open(file, Password);
        Assert.Equal(LocalKey, back.Devices.Single(d => d.Id == "plug").LocalKey);
        Assert.Equal("tuya-secret", back.Cloud.ClientSecret);
        Assert.Equal("st-refresh", back.SmartThings.RefreshToken);
        Assert.Equal("Light", back.Theme);
        Assert.Null(back.MatterStorageKey); // this PC's Matter identity never leaves it
        Assert.Equal("1", back.Devices.Single(d => d.Id == "bulb").MatterNodeId);
    }

    [Fact]
    public void WithoutPassword_SecretsAreRemoved_AndContentIsReadable()
    {
        var bytes = SettingsBackup.Export(Sample(), BackupParts.Devices | BackupParts.Automations | BackupParts.Preferences, password: null);
        var text = Encoding.UTF8.GetString(bytes);
        foreach (var secret in new[] { LocalKey, "tuya-secret", "st-secret", "st-refresh", "matter-key-never-exported" })
            Assert.DoesNotContain(secret, text);

        var file = SettingsBackup.Read(bytes);
        Assert.False(file.Header.Encrypted);
        var back = SettingsBackup.Open(file, password: null);
        Assert.Null(back.Devices.Single(d => d.Id == "plug").LocalKey);
        Assert.Equal("tuya-id", back.Cloud.ClientId); // account ids are not secret
        Assert.Equal("Desk plug", back.Devices.Single(d => d.Id == "plug").Name);
    }

    [Fact]
    public void OnlyChosenParts_AreExported()
    {
        var file = SettingsBackup.Read(SettingsBackup.Export(Sample(), BackupParts.Automations, null));
        var back = SettingsBackup.Open(file, null);
        Assert.Empty(back.Devices);
        Assert.Empty(back.Houses);
        Assert.Single(back.Scenes);
        Assert.Null(back.Cloud.ClientId);
        Assert.Equal("Dark", back.Theme); // default, not the exported "Light"
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Secrets_RequireAPassword(string? password) =>
        Assert.Throws<BackupException>(() => SettingsBackup.Export(Sample(), BackupParts.All, password));

    [Fact]
    public void ShortPassword_IsRejected() =>
        Assert.Throws<BackupException>(() => SettingsBackup.Export(Sample(), BackupParts.All, "short"));

    [Fact]
    public void WrongPassword_IsRejected()
    {
        var file = SettingsBackup.Read(SettingsBackup.Export(Sample(), BackupParts.All, Password));
        var ex = Assert.Throws<BackupException>(() => SettingsBackup.Open(file, "not the password"));
        Assert.Contains("Wrong password", ex.Message);
        Assert.Throws<BackupException>(() => SettingsBackup.Open(file, null));
    }

    [Fact]
    public void ChangedContent_IsDetected()
    {
        var json = JsonNode.Parse(SettingsBackup.Export(Sample(), BackupParts.All, Password))!.AsObject();
        var cipher = Convert.FromBase64String(json["content"]!.GetValue<string>());
        cipher[10] ^= 0x01;
        json["content"] = Convert.ToBase64String(cipher);
        var file = SettingsBackup.Read(Encoding.UTF8.GetBytes(json.ToJsonString()));
        Assert.Throws<BackupException>(() => SettingsBackup.Open(file, Password));
    }

    [Fact]
    public void ChangedHeader_IsDetected()
    {
        // the header is authenticated with the content: faking "contains" or "parts" breaks decryption
        var json = JsonNode.Parse(SettingsBackup.Export(Sample(), BackupParts.All, Password))!.AsObject();
        json["header"]!["Summary"]!["Devices"] = 99;
        var file = SettingsBackup.Read(Encoding.UTF8.GetBytes(json.ToJsonString()));
        Assert.Throws<BackupException>(() => SettingsBackup.Open(file, Password));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"something\":\"else\"}")]
    [InlineData("{\"header\":{\"Format\":\"another-app\",\"Version\":1}}")]
    public void ForeignFiles_AreRejected(string content) =>
        Assert.Throws<BackupException>(() => SettingsBackup.Read(Encoding.UTF8.GetBytes(content)));

    [Fact]
    public void NewerFormat_AsksToUpdateTheApp()
    {
        var json = JsonNode.Parse(SettingsBackup.Export(Sample(), BackupParts.Devices, null))!.AsObject();
        json["header"]!["Version"] = SettingsBackup.FormatVersion + 1;
        var ex = Assert.Throws<BackupException>(() => SettingsBackup.Read(Encoding.UTF8.GetBytes(json.ToJsonString())));
        Assert.Contains("newer version", ex.Message);
    }

    // ---------------------------------------------------------------- merge / replace

    private static AppConfig Current() => new()
    {
        Theme = "Dark",
        MatterStorageKey = "this-pc-matter-key",
        Cloud = new CloudSettings { ClientId = "tuya-id", ClientSecret = "tuya-secret-here" },
        Houses = [new HouseConfig { Id = "h1", Name = "Old name", Rooms = ["Attic"] }, new HouseConfig { Id = "h9", Name = "Cabin" }],
        CurrentHouseId = "h9",
        Devices =
        [
            new DeviceConfig { Id = "plug", Name = "Old plug name", HouseId = "h1", LocalKey = "key-only-on-this-pc" },
            new DeviceConfig { Id = "lamp", Name = "Only here", HouseId = "h9" },
        ],
        Scenes = [new LocalScene { Id = "s9", Name = "Mine" }],
    };

    private static AppConfig FileWithoutKeys()
    {
        var bytes = SettingsBackup.Export(Sample(), BackupParts.Devices | BackupParts.Automations | BackupParts.Preferences, null);
        return SettingsBackup.Open(SettingsBackup.Read(bytes), null);
    }

    [Fact]
    public void Merge_AddsAndUpdates_KeepsTheRest()
    {
        var r = SettingsBackup.Merge(Current(), FileWithoutKeys(), BackupParts.Devices | BackupParts.Automations | BackupParts.Preferences, ImportMode.Merge);
        Assert.Equal(["plug", "lamp", "bulb"], r.Devices.Select(d => d.Id));
        Assert.Equal("Desk plug", r.Devices.Single(d => d.Id == "plug").Name);        // file wins
        Assert.Equal("key-only-on-this-pc", r.Devices.Single(d => d.Id == "plug").LocalKey); // key kept: file had none
        var home = r.Houses.Single(h => h.Id == "h1");
        Assert.Equal("Home", home.Name);
        Assert.Equal(["Kitchen", "Hall", "Attic"], home.Rooms);                        // rooms from both
        Assert.Contains(r.Houses, h => h.Id == "h9");
        Assert.Equal(["s9", "s1"], r.Scenes.Select(s => s.Id));
        Assert.Equal("Light", r.Theme);
        Assert.Equal("h1", r.CurrentHouseId);
        Assert.Equal("tuya-secret-here", r.Cloud.ClientSecret);                          // same Tuya app: secret kept
        Assert.Equal("this-pc-matter-key", r.MatterStorageKey);
    }

    [Fact]
    public void Replace_UsesOnlyTheFile_ForThePartsItCarries()
    {
        var r = SettingsBackup.Merge(Current(), FileWithoutKeys(), BackupParts.Devices, ImportMode.Replace);
        Assert.Equal(["plug", "bulb"], r.Devices.Select(d => d.Id));
        Assert.Equal(["h1"], r.Houses.Select(h => h.Id));
        Assert.Equal("h1", r.CurrentHouseId);                                           // h9 is gone: falls back
        Assert.Equal(["s9"], r.Scenes.Select(s => s.Id));                               // scenes not in the file: untouched
        Assert.Equal("Dark", r.Theme);                                                  // preferences not in the file
        Assert.Equal("key-only-on-this-pc", r.Devices.Single(d => d.Id == "plug").LocalKey);
        Assert.Equal("this-pc-matter-key", r.MatterStorageKey);
    }

    [Fact]
    public void Merge_DoesNotChangeItsInputs()
    {
        var current = Current();
        var incoming = FileWithoutKeys();
        SettingsBackup.Merge(current, incoming, BackupParts.All, ImportMode.Replace);
        Assert.Equal("Old plug name", current.Devices[0].Name);
        Assert.Equal(2, incoming.Devices.Count);
    }

    [Fact]
    public void KeysFromTheFile_ReplaceTheOnesHere()
    {
        var file = SettingsBackup.Open(SettingsBackup.Read(SettingsBackup.Export(Sample(), BackupParts.All, Password)), Password);
        var r = SettingsBackup.Merge(Current(), file, BackupParts.All, ImportMode.Merge);
        Assert.Equal(LocalKey, r.Devices.Single(d => d.Id == "plug").LocalKey);
        Assert.Equal("st-refresh", r.SmartThings.RefreshToken);
        Assert.Equal("this-pc-matter-key", r.MatterStorageKey);
    }

    // ---------------------------------------------------------------- safety copies (undo)

    [Fact]
    public void SafetyCopy_SavesAndRestoresTheConfigFile_KeepingTheNewestTen()
    {
        var store = TestStores.Create(Path.Combine(_dir, "config.json"));
        var before = Current();
        store.Save(before);
        Assert.Null(SettingsBackup.SafetyCopies(store).FirstOrDefault());

        var t0 = new DateTimeOffset(2026, 11, 16, 10, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 12; i++) SettingsBackup.SaveSafetyCopy(store, now: t0.AddMinutes(i));
        var copies = SettingsBackup.SafetyCopies(store);
        Assert.Equal(10, copies.Count);
        Assert.Contains("20261116-101100", copies[0]); // newest first

        store.Save(new AppConfig { Theme = "Light" }); // "the import"
        SettingsBackup.RestoreSafetyCopy(store, copies[0]);
        var restored = store.Load();
        Assert.Equal("Dark", restored.Theme);
        Assert.Equal("key-only-on-this-pc", restored.Devices.Single(d => d.Id == "plug").LocalKey); // still decryptable
        Assert.Equal(9, SettingsBackup.SafetyCopies(store).Count); // the used copy is removed
    }

    [Fact]
    public void SuggestedFileName_HasDateAndExtension() =>
        Assert.Equal("iot-smart-control-2026-11-16.iscbackup", SettingsBackup.SuggestedFileName(new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero)));
}
