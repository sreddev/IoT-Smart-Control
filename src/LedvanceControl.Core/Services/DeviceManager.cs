using System.Text.Json;
using LedvanceControl.Core.Cloud;
using LedvanceControl.Core.Lights;
using LedvanceControl.Core.Local;
using LedvanceControl.Core.Model;

namespace LedvanceControl.Core.Services;

/// <summary>
/// Owns the configuration, the device runtimes and the optional cloud client.
/// Everything the UI does goes through here.
/// </summary>
public sealed class DeviceManager : IAsyncDisposable
{
    private readonly ConfigStore _store;
    private readonly Dictionary<string, DeviceRuntime> _devices = new();
    private readonly object _saveGate = new();

    public DeviceManager(ConfigStore store)
    {
        _store = store;
        Config = store.Load();
        foreach (var d in Config.Devices) _devices[d.Id] = new DeviceRuntime(this, d);
        RecreateCloud();
        RecreateSmartThings();
    }

    public AppConfig Config { get; }
    public string ConfigPath => _store.Path;
    public TuyaCloudClient? Cloud { get; private set; }
    public SmartThingsClient? SmartThings { get; private set; }
    public IReadOnlyList<CloudScene> CloudScenes { get; private set; } = [];
    public IReadOnlyList<CloudAutomation> CloudAutomations { get; private set; } = [];

    public IEnumerable<DeviceRuntime> Devices => _devices.Values;
    public DeviceRuntime? Find(string id) => _devices.GetValueOrDefault(id);

    /// <summary>Raised when devices are added/removed or settings change.</summary>
    public event Action? ListChanged;

    public void Save()
    {
        lock (_saveGate) _store.Save(Config);
    }

    // ---------------------------------------------------------------- cloud

    public void RecreateCloud()
    {
        Cloud?.Dispose();
        var c = Config.Cloud;
        Cloud = !string.IsNullOrWhiteSpace(c.ClientId) && !string.IsNullOrWhiteSpace(c.ClientSecret)
            ? new TuyaCloudClient(c.Region, c.ClientId.Trim(), c.ClientSecret.Trim())
            : null;
    }

    public void RecreateSmartThings()
    {
        SmartThings?.Dispose();
        var s = Config.SmartThings;
        if (string.IsNullOrWhiteSpace(s.ClientId) || string.IsNullOrWhiteSpace(s.ClientSecret))
        {
            SmartThings = null;
            return;
        }
        SmartThings = new SmartThingsClient(s.ClientId.Trim(), s.ClientSecret.Trim(), s.RefreshToken);
        SmartThings.TokensChanged += t =>
        {
            Config.SmartThings.RefreshToken = t.RefreshToken;
            Save();
        };
    }

    /// <summary>Pulls devices (and rooms) from SmartThings and adds them as SmartThings-backed devices.</summary>
    public async Task SyncFromSmartThingsAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var st = SmartThings ?? throw new InvalidOperationException("SmartThings is not connected (Settings → Connect).");
        progress?.Report("Loading SmartThings devices…");
        var devices = await st.GetDevicesAsync(ct);

        var rooms = new Dictionary<string, string>();
        foreach (var loc in devices.Select(d => d.LocationId).OfType<string>().Distinct())
            foreach (var r in await st.GetRoomsAsync(loc, ct))
                rooms[r.RoomId] = r.Name;

        int count = 0;
        foreach (var d in devices.Where(d => d.Capabilities.Contains(SmartThingsMapping.CapSwitch)))
        {
            var cfg = Config.Devices.FirstOrDefault(c => c.Id == d.DeviceId) ?? new DeviceConfig { Id = d.DeviceId };
            if (!Config.Devices.Contains(cfg)) Config.Devices.Add(cfg);
            cfg.Name = d.Label;
            cfg.SmartThingsId = d.DeviceId;
            cfg.Category = SmartThingsMapping.GuessCategory(d.Capabilities);
            cfg.Room = d.RoomId != null && rooms.TryGetValue(d.RoomId, out var rn) ? rn : cfg.Room;
            cfg.Schema = SmartThingsMapping.BuildSchema(d.Capabilities);
            cfg.PreferLocal = false;
            if (_devices.TryGetValue(d.DeviceId, out var rt)) rt.SchemaChanged();
            else _devices[d.DeviceId] = new DeviceRuntime(this, cfg);
            count++;
        }
        Save();
        ListChanged?.Invoke();
        progress?.Report($"Synchronised {count} SmartThings device(s).");
    }

    /// <summary>Pulls devices, local keys, schemas, rooms, scenes and automations from the cloud project.</summary>
    public async Task SyncFromCloudAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var cloud = Cloud ?? throw new InvalidOperationException("Cloud access is not configured (Settings).");
        progress?.Report("Loading device list…");
        var devices = await cloud.GetDevicesAsync(ct);
        foreach (var cd in devices)
        {
            var cfg = Config.Devices.FirstOrDefault(d => d.Id == cd.Id);
            if (cfg == null)
            {
                cfg = new DeviceConfig { Id = cd.Id };
                Config.Devices.Add(cfg);
            }
            cfg.Name = cd.Name;
            cfg.LocalKey = cd.LocalKey ?? cfg.LocalKey;
            cfg.Category = cd.Category;
            cfg.ProductId = cd.ProductId;
            cfg.ProductName = cd.ProductName;
            cfg.HomeId = cd.HomeId;
            cfg.Icon = cd.Icon;
            cfg.InCloud = true;
            if (cd.Uid != null) Config.Cloud.UserId ??= cd.Uid;

            progress?.Report($"Reading data points of {cd.Name}…");
            try
            {
                var schema = await cloud.GetDeviceSchemaAsync(cd.Id, ct);
                if (schema.Count > 0) cfg.Schema = schema;
            }
            catch (TuyaCloudException) { /* keep previous/guessed schema */ }
            if (cfg.Schema.Count == 0) cfg.Schema = DefaultSchemas.Guess(cfg.Category, null);

            if (_devices.TryGetValue(cd.Id, out var rt)) rt.SchemaChanged();
            else _devices[cd.Id] = new DeviceRuntime(this, cfg);
        }

        foreach (var uid in devices.Select(d => d.Uid).OfType<string>().Distinct())
        {
            progress?.Report("Loading homes and rooms…");
            List<CloudHome> homes;
            try { homes = await cloud.GetHomesAsync(uid, ct); }
            catch (TuyaCloudException) { continue; }
            foreach (var home in homes)
            {
                try
                {
                    foreach (var room in await cloud.GetRoomsAsync(home.HomeId, ct))
                    foreach (var id in room.DeviceIds)
                        if (Config.Devices.FirstOrDefault(d => d.Id == id) is { } dev) dev.Room = room.Name;
                }
                catch (TuyaCloudException) { }
            }
        }
        Save();
        ListChanged?.Invoke();
        await RefreshCloudScenesAsync(ct);
        progress?.Report($"Synchronised {devices.Count} device(s).");
    }

    public async Task RefreshCloudScenesAsync(CancellationToken ct = default)
    {
        if (Cloud == null) return;
        var scenes = new List<CloudScene>();
        var autos = new List<CloudAutomation>();
        foreach (var homeId in Config.Devices.Select(d => d.HomeId).OfType<string>().Distinct())
        {
            try { scenes.AddRange(await Cloud.GetScenesAsync(homeId, ct)); } catch (TuyaCloudException) { }
            try { autos.AddRange(await Cloud.GetAutomationsAsync(homeId, ct)); } catch (TuyaCloudException) { }
        }
        CloudScenes = scenes;
        CloudAutomations = autos;
        ListChanged?.Invoke();
    }

    // ---------------------------------------------------------------- LAN

    /// <summary>Listens for device broadcasts and updates IP/protocol version of known devices.</summary>
    public async Task<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(TimeSpan duration, CancellationToken ct = default)
    {
        var found = await TuyaDiscovery.ScanAsync(duration, null, ct);
        foreach (var f in found)
        {
            var cfg = Config.Devices.FirstOrDefault(d => d.Id == f.Id);
            if (cfg == null) continue;
            cfg.Ip = f.Ip;
            cfg.Version = f.Version;
        }
        Save();
        ListChanged?.Invoke();
        return found;
    }

    // ---------------------------------------------------------------- device CRUD

    public DeviceRuntime AddOrUpdate(DeviceConfig cfg)
    {
        var existing = Config.Devices.FindIndex(d => d.Id == cfg.Id);
        if (existing >= 0) Config.Devices[existing] = cfg;
        else Config.Devices.Add(cfg);
        if (_devices.Remove(cfg.Id, out var old)) _ = old.DisposeAsync().AsTask();
        var rt = new DeviceRuntime(this, cfg);
        _devices[cfg.Id] = rt;
        Save();
        ListChanged?.Invoke();
        return rt;
    }

    public void Remove(string id)
    {
        Config.Devices.RemoveAll(d => d.Id == id);
        foreach (var g in Config.Groups) g.DeviceIds.Remove(id);
        if (_devices.Remove(id, out var rt)) _ = rt.DisposeAsync().AsTask();
        Save();
        ListChanged?.Invoke();
    }

    public int ImportTinyTuya(string path)
    {
        var imported = TinyTuyaImporter.Import(File.ReadAllText(path));
        foreach (var dev in imported)
        {
            var cur = Config.Devices.FirstOrDefault(d => d.Id == dev.Id);
            if (cur != null)
            {
                // keep what we know, fill the gaps
                dev.InCloud = cur.InCloud;
                dev.Room ??= cur.Room;
                dev.HomeId ??= cur.HomeId;
                dev.Ip ??= cur.Ip;
                if (dev.Schema.Count == 0) dev.Schema = cur.Schema;
            }
            AddOrUpdate(dev);
        }
        return imported.Count;
    }

    // ---------------------------------------------------------------- groups & scenes

    public IEnumerable<DeviceRuntime> Members(GroupConfig g) => g.DeviceIds.Select(Find).OfType<DeviceRuntime>();

    /// <summary>Applies a per-device command to all members in parallel; returns the failures.</summary>
    public async Task<IReadOnlyList<string>> ForEachAsync(IEnumerable<DeviceRuntime> devices, Func<DeviceRuntime, Task> action)
    {
        var errors = new List<string>();
        await Task.WhenAll(devices.Select(async d =>
        {
            try { await action(d); }
            catch (Exception ex) { lock (errors) errors.Add($"{d.Config.Name}: {ex.Message}"); }
        }));
        return errors;
    }

    public Task<IReadOnlyList<string>> SetGroupPowerAsync(GroupConfig g, bool on) =>
        ForEachAsync(Members(g), d => d.SetPowerAsync(on));

    /// <summary>Applies a light command (built per device from its own profile) to every light in the group.</summary>
    public Task<IReadOnlyList<string>> SetGroupLightAsync(GroupConfig g, Func<LightProfile, IReadOnlyDictionary<string, object?>> build) =>
        ForEachAsync(Members(g).Where(d => d.Light != null), d => d.SetAsync(build(d.Light!)));

    /// <summary>Creates a local scene from the current state of the given devices.</summary>
    public LocalScene CaptureScene(string name, IEnumerable<string> deviceIds)
    {
        var scene = new LocalScene { Name = name };
        foreach (var d in deviceIds.Select(Find).OfType<DeviceRuntime>())
        {
            var writable = d.Config.Schema.Where(s => s.CanWrite && !s.Code.StartsWith("countdown", StringComparison.Ordinal)).Select(s => s.Code).ToHashSet();
            var values = d.Snapshot()
                .Where(kv => writable.Contains(kv.Key) && kv.Value != null)
                .Where(kv => d.Light?.ModeCode != kv.Key || kv.Value is not "music")
                .ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value));
            // when the light is off, only store "off"
            if (d.PowerCode != null && d.Get(d.PowerCode) is false)
                values = new() { [d.PowerCode] = JsonSerializer.SerializeToElement(false) };
            if (values.Count > 0) scene.Actions.Add(new SceneAction { DeviceId = d.Id, Values = values });
        }
        Config.Scenes.Add(scene);
        Save();
        return scene;
    }

    public async Task<IReadOnlyList<string>> RunSceneAsync(LocalScene scene)
    {
        var errors = new List<string>();
        await Task.WhenAll(scene.Actions.Select(async a =>
        {
            var d = Find(a.DeviceId);
            if (d == null) return;
            try
            {
                await d.SetAsync(a.Values.ToDictionary(kv => kv.Key, kv => DeviceRuntime.FromJson(kv.Value)));
            }
            catch (Exception ex)
            {
                lock (errors) errors.Add($"{d.Config.Name}: {ex.Message}");
            }
        }));
        return errors;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var d in _devices.Values) await d.DisposeAsync();
        Cloud?.Dispose();
        SmartThings?.Dispose();
    }
}
