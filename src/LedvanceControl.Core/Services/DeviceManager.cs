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
public sealed partial class DeviceManager : IAsyncDisposable
{
    private readonly ConfigStore _store;
    private readonly Dictionary<string, DeviceRuntime> _devices = new();
    private readonly object _saveGate = new();

    /// <param name="matter">Where the Matter bridge lives; null disables Matter (e.g. Node.js not installed).</param>
    public DeviceManager(ConfigStore store, MatterBridgeOptions? matter = null)
    {
        _store = store;
        Config = store.Load();
        if (EnsureHouses()) Save();
        foreach (var d in Config.Devices) _devices[d.Id] = new DeviceRuntime(this, d);
        RecreateCloud();
        RecreateSmartThings();
        if (matter != null)
        {
            Matter = new MatterBridgeClient(matter);
            Matter.StateReceived += (nodeId, state) => FindMatter(nodeId)?.ApplyMatterState(state);
            Matter.ConnectionChanged += (nodeId, status) => FindMatter(nodeId)?.ApplyMatterConnection(status);
        }
    }

    public AppConfig Config { get; }
    public string ConfigPath => _store.Path;
    public TuyaCloudClient? Cloud { get; private set; }
    public SmartThingsClient? SmartThings { get; private set; }
    /// <summary>The Matter controller bridge, or null when Node.js / the bridge is not available.</summary>
    public MatterBridgeClient? Matter { get; }
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

        // Each SmartThings location is a house; its rooms are that house's rooms.
        var rooms = new Dictionary<string, string>();
        var houseByLocation = new Dictionary<string, HouseConfig>();
        foreach (var loc in devices.Select(d => d.LocationId).OfType<string>().Distinct())
        {
            var house = LinkHouse(HouseSource.SmartThings, loc, await st.GetLocationNameAsync(loc, ct));
            houseByLocation[loc] = house;
            var locRooms = await st.GetRoomsAsync(loc, ct);
            foreach (var r in locRooms) rooms[r.RoomId] = r.Name;
            MergeRooms(house, locRooms.Select(r => r.Name));
        }

        int count = 0;
        foreach (var d in devices.Where(d => d.Capabilities.Contains(SmartThingsMapping.CapSwitch)))
        {
            var cfg = Config.Devices.FirstOrDefault(c => c.Id == d.DeviceId) ?? new DeviceConfig { Id = d.DeviceId };
            if (!Config.Devices.Contains(cfg)) Config.Devices.Add(cfg);
            var houseId = d.LocationId != null && houseByLocation.TryGetValue(d.LocationId, out var h) ? h.Id : cfg.HouseId ?? CurrentHouse.Id;
            ApplySynced(cfg, d.Label, d.RoomId != null && rooms.TryGetValue(d.RoomId, out var rn) ? rn : null, houseId);
            cfg.SmartThingsId = d.DeviceId;
            cfg.Category = SmartThingsMapping.GuessCategory(d.Capabilities);
            cfg.Schema = SmartThingsMapping.BuildSchema(d.Capabilities);
            cfg.PreferLocal = false;
            if (_devices.TryGetValue(d.DeviceId, out var rt)) rt.SchemaChanged();
            else _devices[d.DeviceId] = new DeviceRuntime(this, cfg);
            count++;
        }

        // Prune SmartThings-only devices that no longer exist at the source (e.g. removed in the
        // SmartThings app). Tuya-synced (InCloud) and manually-added devices are left untouched.
        var liveIds = devices.Select(d => d.DeviceId).ToHashSet();
        var stale = Config.Devices
            .Where(c => c.SmartThingsId != null && !c.InCloud && !liveIds.Contains(c.SmartThingsId!))
            .Select(c => c.Id).ToList();
        foreach (var id in stale) RemoveInternal(id);

        Save();
        ListChanged?.Invoke();
        var msg = $"Synchronised {count} SmartThings device(s)";
        if (stale.Count > 0) msg += $", removed {stale.Count} no longer present";
        progress?.Report(msg + ".");
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
            ApplySynced(cfg, cd.Name, room: null);
            cfg.HouseId ??= CurrentHouse.Id; // refined below once the Tuya homes are known
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
                // Each Tuya home is a house; its devices live there.
                var house = LinkHouse(HouseSource.Tuya, home.HomeId, home.Name);
                foreach (var dev in Config.Devices.Where(d => d.InCloud && d.HomeId == home.HomeId))
                    ApplySynced(dev, name: null, room: null, house.Id);
                try
                {
                    var cloudRooms = await cloud.GetRoomsAsync(home.HomeId, ct);
                    MergeRooms(house, cloudRooms.Select(r => r.Name));
                    foreach (var room in cloudRooms)
                    foreach (var id in room.DeviceIds)
                        if (Config.Devices.FirstOrDefault(d => d.Id == id) is { } dev) ApplySynced(dev, name: null, room.Name, house.Id);
                }
                catch (TuyaCloudException) { }
            }
        }
        // Prune Tuya-synced devices removed from the cloud project (keep SmartThings/manual devices).
        var liveIds = devices.Select(d => d.Id).ToHashSet();
        var stale = Config.Devices.Where(c => c.InCloud && !liveIds.Contains(c.Id)).Select(c => c.Id).ToList();
        foreach (var id in stale) RemoveInternal(id);

        Save();
        ListChanged?.Invoke();
        await RefreshCloudScenesAsync(ct);
        var msg = $"Synchronised {devices.Count} device(s)";
        if (stale.Count > 0) msg += $", removed {stale.Count} no longer in the project";
        progress?.Report(msg + ".");
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

    // ---------------------------------------------------------------- Matter

    public const string MatterIdPrefix = "matter-";

    private DeviceRuntime? FindMatter(string nodeId) =>
        _devices.Values.FirstOrDefault(d => d.Config.MatterNodeId == nodeId);

    private MatterBridgeClient RequireMatter() => Matter
        ?? throw new InvalidOperationException("Matter needs Node.js (nodejs.org, LTS). Install it and restart the app.");

    /// <summary>
    /// Pairs a Matter device into this PC's own fabric (multi-admin: it stays paired with its other
    /// apps) and adds it as a device. The device must have a pairing window open.
    /// </summary>
    public async Task<DeviceRuntime> PairMatterAsync(string pairingCode, string? ip = null, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var bridge = RequireMatter();
        progress?.Report("Pairing over Matter (this can take up to a minute)…");
        var info = await bridge.CommissionAsync(pairingCode, ip, ct);
        var rt = UpsertMatter(info, renameExisting: true);
        Save();
        ListChanged?.Invoke();
        if (info.State != null) rt.ApplyMatterState(info.State);
        progress?.Report($"Paired '{rt.Config.Name}' over Matter.");
        return rt;
    }

    /// <summary>Re-reads the list of Matter devices paired with this PC and adds/prunes devices accordingly.</summary>
    public async Task SyncMatterAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var bridge = RequireMatter();
        progress?.Report("Reading Matter devices…");
        var nodes = await bridge.GetNodesAsync(ct);
        foreach (var n in nodes) UpsertMatter(n, renameExisting: false);

        var live = nodes.Select(n => n.NodeId).ToHashSet();
        var stale = Config.Devices.Where(c => c.MatterNodeId != null && !live.Contains(c.MatterNodeId)).Select(c => c.Id).ToList();
        foreach (var id in stale) RemoveInternal(id);

        Save();
        ListChanged?.Invoke();
        var msg = $"{nodes.Count} Matter device(s) paired with this PC";
        if (stale.Count > 0) msg += $", removed {stale.Count} no longer paired";
        progress?.Report(msg + ".");
    }

    public Task<IReadOnlyList<MatterCommissionableDevice>> DiscoverMatterAsync(TimeSpan duration, CancellationToken ct = default) =>
        RequireMatter().DiscoverAsync(duration, ct);

    private DeviceRuntime UpsertMatter(MatterNodeInfo info, bool renameExisting)
    {
        var id = MatterIdPrefix + info.NodeId;
        var cfg = Config.Devices.FirstOrDefault(c => c.MatterNodeId == info.NodeId) ?? Config.Devices.FirstOrDefault(c => c.Id == id);
        bool isNew = cfg == null;
        cfg ??= new DeviceConfig { Id = id, Name = info.Name, HouseId = CurrentHouse.Id }; // paired into the selected house
        if (isNew) Config.Devices.Add(cfg);
        else if (renameExisting && !string.IsNullOrWhiteSpace(info.Name)) ApplySynced(cfg, info.Name, room: null);

        cfg.MatterNodeId = info.NodeId;
        cfg.ProductName = info.ProductName ?? cfg.ProductName;
        cfg.PreferLocal = true;
        if (info.Capabilities is { } caps)
        {
            cfg.Category = MatterMapping.GuessCategory(caps);
            cfg.Schema = MatterMapping.BuildSchema(caps);
        }
        else if (cfg.Schema.Count == 0)
        {
            cfg.Category ??= "dj";
            cfg.Schema = MatterMapping.BuildSchema(new MatterCapabilities { Level = true });
        }

        if (_devices.TryGetValue(cfg.Id, out var rt))
        {
            rt.SchemaChanged();
            return rt;
        }
        rt = new DeviceRuntime(this, cfg);
        _devices[cfg.Id] = rt;
        return rt;
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
        if (cfg.HouseId == null || FindHouse(cfg.HouseId) == null) cfg.HouseId = CurrentHouse.Id;
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
        // A Matter device is also removed from this PC's fabric (its other controllers keep it).
        if (Find(id)?.Config.MatterNodeId is { } nodeId && Matter != null)
            _ = Matter.RemoveAsync(nodeId).ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        RemoveInternal(id);
        Save();
        ListChanged?.Invoke();
    }

    /// <summary>Removes a device from config, groups and the runtime map without persisting/notifying.</summary>
    private void RemoveInternal(string id)
    {
        Config.Devices.RemoveAll(d => d.Id == id);
        foreach (var g in Config.Groups) g.DeviceIds.Remove(id);
        if (_devices.Remove(id, out var rt)) _ = rt.DisposeAsync().AsTask();
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
        if (Matter != null) await Matter.DisposeAsync();
    }
}
