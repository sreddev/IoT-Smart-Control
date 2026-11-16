using LedvanceControl.Core.Lights;
using LedvanceControl.Core.Model;

namespace LedvanceControl.Core.Services;

/// <summary>Which cloud a house mirrors (used when a sync links a cloud home to a house).</summary>
public enum HouseSource { SmartThings, Tuya }

/// <summary>Whether a device may be moved to another house in this app, and why not.</summary>
public sealed record HouseMoveRule(bool Allowed, bool NeedsSmartPlusConfirmation, string? Reason)
{
    public static readonly HouseMoveRule Free = new(true, false, null);
}

/// <summary>
/// The House → Room → Device hierarchy: migration of single-house configs, house and room CRUD,
/// cloud-home linking for syncs, and the rules for moving devices between houses.
/// </summary>
public sealed partial class DeviceManager
{
    private static readonly StringComparer RoomComparer = StringComparer.CurrentCultureIgnoreCase;
    private static bool Same(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.CurrentCultureIgnoreCase);

    // ---------------------------------------------------------------- houses

    public IReadOnlyList<HouseConfig> Houses => Config.Houses;
    public HouseConfig? FindHouse(string? id) => id == null ? null : Config.Houses.FirstOrDefault(h => h.Id == id);

    /// <summary>The house shown on the Devices / Rooms / Groups pages (always exists).</summary>
    public HouseConfig CurrentHouse => FindHouse(Config.CurrentHouseId) ?? Config.Houses[0];

    public IEnumerable<DeviceRuntime> DevicesInHouse(string houseId) => _devices.Values.Where(d => d.Config.HouseId == houseId);

    /// <summary>
    /// Makes sure there is at least one house and every device / group / room belongs to one.
    /// Migrates the legacy single-house fields (HomeName, Rooms). Returns true when the config changed.
    /// </summary>
    private bool EnsureHouses()
    {
        var changed = false;
        if (Config.Houses.Count == 0)
        {
            Config.Houses.Add(new HouseConfig
            {
                Name = string.IsNullOrWhiteSpace(Config.HomeName) ? "My home" : Config.HomeName.Trim(),
                NameEdited = Config.HomeNameEdited == true,
                Rooms = Config.Rooms?.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()).Distinct(RoomComparer).ToList() ?? [],
            });
            changed = true;
        }
        if (Config.HomeName != null || Config.HomeNameEdited != null || Config.Rooms != null)
        {
            Config.HomeName = null;
            Config.HomeNameEdited = null;
            Config.Rooms = null;
            changed = true;
        }
        var fallback = Config.Houses[0];
        foreach (var d in Config.Devices)
        {
            if (FindHouse(d.HouseId) == null) { d.HouseId = fallback.Id; changed = true; }
            if (!string.IsNullOrWhiteSpace(d.Room))
            {
                var house = FindHouse(d.HouseId)!;
                if (!house.Rooms.Contains(d.Room.Trim(), RoomComparer)) { house.Rooms.Add(d.Room.Trim()); changed = true; }
            }
        }
        foreach (var g in Config.Groups.Where(g => FindHouse(g.HouseId) == null)) { g.HouseId = fallback.Id; changed = true; }
        if (FindHouse(Config.CurrentHouseId) == null) { Config.CurrentHouseId = fallback.Id; changed = true; }
        return changed;
    }

    public void SetCurrentHouse(string houseId)
    {
        if (FindHouse(houseId) == null || Config.CurrentHouseId == houseId) return;
        Config.CurrentHouseId = houseId;
        Save();
        ListChanged?.Invoke();
    }

    public HouseConfig AddHouse(string name)
    {
        name = RequireName(name, "house");
        if (Config.Houses.Any(h => Same(h.Name, name))) throw new InvalidOperationException($"A house called '{name}' already exists.");
        var house = new HouseConfig { Name = name, NameEdited = true };
        Config.Houses.Add(house);
        Save();
        ListChanged?.Invoke();
        return house;
    }

    /// <summary>Renames a house; later syncs of its SmartThings location / Tuya home keep the new name.</summary>
    public void RenameHouse(string houseId, string name)
    {
        var house = FindHouse(houseId) ?? throw new InvalidOperationException("Unknown house.");
        name = RequireName(name, "house");
        if (Config.Houses.Any(h => h != house && Same(h.Name, name))) throw new InvalidOperationException($"A house called '{name}' already exists.");
        if (house.Name == name && house.NameEdited) return;
        house.Name = name;
        house.NameEdited = true;
        Save();
        ListChanged?.Invoke();
    }

    /// <summary>
    /// Deletes a house with its rooms and groups. The last house can't be deleted, and neither can a
    /// house that still has devices (they would be left without a house).
    /// </summary>
    public void DeleteHouse(string houseId)
    {
        var house = FindHouse(houseId) ?? throw new InvalidOperationException("Unknown house.");
        if (Config.Houses.Count == 1)
            throw new InvalidOperationException($"'{house.Name}' is your only house, so it can't be deleted. Rename it instead.");
        var devices = Config.Devices.Count(d => d.HouseId == houseId);
        if (devices > 0)
            throw new InvalidOperationException($"'{house.Name}' still has {devices} device(s). Remove them first, or move them to another house.");
        var groupIds = Config.Groups.Where(g => g.HouseId == houseId).Select(g => g.Id).ToHashSet();
        Config.Groups.RemoveAll(g => groupIds.Contains(g.Id));
        Config.Schedules.RemoveAll(s => s.TargetType == ScheduleTarget.Group && groupIds.Contains(s.TargetId));
        Config.Houses.Remove(house);
        if (Config.CurrentHouseId == houseId) Config.CurrentHouseId = Config.Houses[0].Id;
        Save();
        ListChanged?.Invoke();
    }

    /// <summary>
    /// Finds (or creates) the house mirroring a SmartThings location / Tuya home. An unlinked house with
    /// the same name, or the only house when it is still unlinked, is adopted; otherwise a new house is
    /// created. The house takes the cloud's name unless the user renamed it here.
    /// </summary>
    private HouseConfig LinkHouse(HouseSource source, string cloudId, string? cloudName)
    {
        string? LinkOf(HouseConfig h) => source == HouseSource.SmartThings ? h.SmartThingsLocationId : h.TuyaHomeId;
        var house = Config.Houses.FirstOrDefault(h => LinkOf(h) == cloudId);
        if (house == null)
        {
            house = Config.Houses.FirstOrDefault(h => LinkOf(h) == null && cloudName != null && Same(h.Name, cloudName))
                    ?? (Config.Houses.Count == 1 && LinkOf(Config.Houses[0]) == null ? Config.Houses[0] : null);
            if (house == null)
            {
                house = new HouseConfig { Name = string.IsNullOrWhiteSpace(cloudName) ? "Home" : cloudName.Trim() };
                Config.Houses.Add(house);
            }
            if (source == HouseSource.SmartThings) house.SmartThingsLocationId = cloudId;
            else house.TuyaHomeId = cloudId;
        }
        if (!house.NameEdited && !string.IsNullOrWhiteSpace(cloudName)) house.Name = cloudName.Trim();
        return house;
    }

    // ---------------------------------------------------------------- rooms

    /// <summary>Rooms of a house: its stored list plus any room one of its devices is in, sorted.</summary>
    public IReadOnlyList<string> RoomsOf(string houseId)
    {
        var house = FindHouse(houseId);
        if (house == null) return [];
        return house.Rooms.Concat(Config.Devices.Where(d => d.HouseId == houseId).Select(d => d.Room))
            .Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r!.Trim())
            .Distinct(RoomComparer).Order(RoomComparer).ToList();
    }

    public IEnumerable<DeviceRuntime> DevicesInRoom(string houseId, string room) =>
        _devices.Values.Where(d => d.Config.HouseId == houseId && Same(d.Config.Room, room));

    private static void MergeRooms(HouseConfig house, IEnumerable<string?> rooms)
    {
        foreach (var r in rooms)
            if (!string.IsNullOrWhiteSpace(r) && !house.Rooms.Contains(r.Trim(), RoomComparer)) house.Rooms.Add(r.Trim());
    }

    public void AddRoom(string houseId, string room)
    {
        var house = FindHouse(houseId) ?? throw new InvalidOperationException("Unknown house.");
        room = RequireName(room, "room");
        if (RoomsOf(houseId).Contains(room, RoomComparer)) throw new InvalidOperationException($"{house.Name} already has a room called '{room}'.");
        house.Rooms.Add(room);
        Save();
        ListChanged?.Invoke();
    }

    /// <summary>Renames a room of one house; its devices move along (and keep the assignment across syncs).</summary>
    public void RenameRoom(string houseId, string oldName, string newName)
    {
        var house = FindHouse(houseId) ?? throw new InvalidOperationException("Unknown house.");
        newName = RequireName(newName, "room");
        if (!Same(oldName, newName) && RoomsOf(houseId).Contains(newName, RoomComparer))
            throw new InvalidOperationException($"{house.Name} already has a room called '{newName}'.");
        house.Rooms.RemoveAll(r => Same(r, oldName));
        house.Rooms.Add(newName);
        foreach (var d in Config.Devices.Where(d => d.HouseId == houseId && Same(d.Room, oldName)))
        {
            d.Room = newName;
            d.RoomEdited = true;
        }
        Save();
        ListChanged?.Invoke();
    }

    /// <summary>Deletes a room of one house; its devices go to "No room" (and stay there across syncs).</summary>
    public void DeleteRoom(string houseId, string room)
    {
        var house = FindHouse(houseId) ?? throw new InvalidOperationException("Unknown house.");
        house.Rooms.RemoveAll(r => Same(r, room));
        foreach (var d in Config.Devices.Where(d => d.HouseId == houseId && Same(d.Room, room)))
        {
            d.Room = null;
            d.RoomEdited = true;
        }
        Save();
        ListChanged?.Invoke();
    }

    public Task<IReadOnlyList<string>> SetRoomPowerAsync(string houseId, string room, bool on) =>
        ForEachAsync(DevicesInRoom(houseId, room).Where(d => d.PowerCode != null), d => d.SetPowerAsync(on));

    /// <summary>Applies a light command (built per device from its own profile) to every light in the room.</summary>
    public Task<IReadOnlyList<string>> SetRoomLightAsync(string houseId, string room, Func<LightProfile, IReadOnlyDictionary<string, object?>> build) =>
        ForEachAsync(DevicesInRoom(houseId, room).Where(d => d.Light != null), d => d.SetAsync(build(d.Light!)));

    // ---------------------------------------------------------------- device name / room / house

    /// <summary>
    /// Applies a name/room/house coming from a sync source (SmartThings, Tuya, Matter). The house is the
    /// cloud's and always wins (a device moved there clears its room); name and room are kept when the
    /// user changed them in this app. Null or blank values leave a field untouched.
    /// </summary>
    public static void ApplySynced(DeviceConfig cfg, string? name, string? room, string? houseId = null)
    {
        if (houseId != null && cfg.HouseId != houseId)
        {
            cfg.HouseId = houseId;
            cfg.Room = null;
            cfg.RoomEdited = false;
        }
        if (!cfg.NameEdited && !string.IsNullOrWhiteSpace(name)) cfg.Name = name.Trim();
        if (!cfg.RoomEdited && !string.IsNullOrWhiteSpace(room)) cfg.Room = room.Trim();
    }

    /// <summary>Renames a device in this app; later syncs will not overwrite it.</summary>
    public void RenameDevice(string id, string name)
    {
        var cfg = Config.Devices.FirstOrDefault(d => d.Id == id) ?? throw new InvalidOperationException("Unknown device.");
        name = RequireName(name, "device");
        if (cfg.Name == name) return;
        cfg.Name = name;
        cfg.NameEdited = true;
        Save();
        ListChanged?.Invoke();
    }

    /// <summary>Moves a device to a room of its own house (null/blank = no room); a new room name is
    /// added to that house. Later syncs will not overwrite the assignment.</summary>
    public void SetDeviceRoom(string id, string? room)
    {
        var cfg = Config.Devices.FirstOrDefault(d => d.Id == id) ?? throw new InvalidOperationException("Unknown device.");
        var house = FindHouse(cfg.HouseId) ?? CurrentHouse;
        string? target = null;
        if (!string.IsNullOrWhiteSpace(room))
        {
            target = RoomsOf(house.Id).FirstOrDefault(r => Same(r, room)) ?? room.Trim();
            MergeRooms(house, [target]);
        }
        if (cfg.Room == target && cfg.RoomEdited) return;
        cfg.Room = target;
        cfg.RoomEdited = true;
        Save();
        ListChanged?.Invoke();
    }

    /// <summary>
    /// Whether a device may change house in this app. Cloud devices follow their cloud home; Matter bulbs
    /// may move only once the user confirms they moved them in the LEDVANCE SMART+ app (this app can't
    /// see SMART+); manually added devices move freely.
    /// </summary>
    public HouseMoveRule HouseMoveRuleFor(DeviceConfig cfg)
    {
        if (Config.Houses.Count < 2) return new HouseMoveRule(false, false, "Add another house to move devices between houses.");
        if (cfg.SmartThingsId != null)
            return new HouseMoveRule(false, false, "This device's house follows its SmartThings location. Move it in the SmartThings app, then sync.");
        if (cfg.InCloud)
            return new HouseMoveRule(false, false, "This device's house follows its Tuya home. Move it in the Smart Life / Tuya app, then sync.");
        if (cfg.MatterNodeId != null) return new HouseMoveRule(true, true, null);
        return HouseMoveRule.Free;
    }

    /// <summary>Moves a device to another house (its room is cleared, as rooms belong to a house).</summary>
    /// <param name="confirmedInSmartPlus">For Matter bulbs: the user confirmed the move was made in the SMART+ app first.</param>
    public void MoveDeviceToHouse(string id, string houseId, bool confirmedInSmartPlus = false)
    {
        var cfg = Config.Devices.FirstOrDefault(d => d.Id == id) ?? throw new InvalidOperationException("Unknown device.");
        var target = FindHouse(houseId) ?? throw new InvalidOperationException("Unknown house.");
        if (cfg.HouseId == target.Id) return;
        var rule = HouseMoveRuleFor(cfg);
        if (!rule.Allowed) throw new InvalidOperationException(rule.Reason);
        if (rule.NeedsSmartPlusConfirmation && !confirmedInSmartPlus)
            throw new InvalidOperationException($"Move '{cfg.Name}' to {target.Name} in the LEDVANCE SMART+ app first, then confirm here.");
        cfg.HouseId = target.Id;
        cfg.Room = null;
        cfg.RoomEdited = false;
        foreach (var g in Config.Groups.Where(g => g.HouseId != target.Id)) g.DeviceIds.Remove(id); // groups are per house
        Save();
        ListChanged?.Invoke();
    }

    private static string RequireName(string? name, string what) =>
        string.IsNullOrWhiteSpace(name) ? throw new InvalidOperationException($"The {what} name cannot be empty.") : name.Trim();
}
