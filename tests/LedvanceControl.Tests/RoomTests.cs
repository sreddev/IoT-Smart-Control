using LedvanceControl.Core.Model;
using LedvanceControl.Core.Services;
using Xunit;

namespace LedvanceControl.Tests;

/// <summary>House → Room → Device hierarchy: migration, house/room CRUD, sync rules and house moves.</summary>
public sealed class RoomTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "rooms-test-" + Guid.NewGuid().ToString("N"), "config.json");

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_path)!, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private DeviceManager Create(Action<AppConfig>? seed = null)
    {
        var store = TestStores.Create(_path);
        if (seed != null)
        {
            var cfg = store.Load();
            seed(cfg);
            store.Save(cfg);
        }
        return new DeviceManager(store);
    }

    private DeviceManager Create(params DeviceConfig[] devices) => Create(c => c.Devices.AddRange(devices));

    // ---------------------------------------------------------------- migration

    [Fact]
    public async Task LegacySingleHouseConfig_IsMigratedIntoOneHouse()
    {
        // Write a config the way the previous version did (flat HomeName / Rooms, no houses).
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, """
            { "HomeName": "Casa", "HomeNameEdited": true, "Rooms": ["Kitchen", "kitchen", "Hall"],
              "Devices": [ { "Id": "a", "Name": "A", "Room": "Living room" }, { "Id": "b", "Name": "B" } ],
              "Groups": [ { "Id": "g", "Name": "G", "DeviceIds": ["a"] } ] }
            """);
        await using (var m = new DeviceManager(TestStores.Create(_path)))
        {
            var house = Assert.Single(m.Houses);
            Assert.Equal("Casa", house.Name);
            Assert.True(house.NameEdited);
            Assert.Equal(["Hall", "Kitchen", "Living room"], m.RoomsOf(house.Id));
            Assert.All(m.Config.Devices, d => Assert.Equal(house.Id, d.HouseId));
            Assert.Equal(house.Id, m.Config.Groups[0].HouseId);
            Assert.Equal(house.Id, m.CurrentHouse.Id);
        }
        var json = File.ReadAllText(_path);
        Assert.DoesNotContain("\"HomeName\"", json);   // legacy fields are not written back
        Assert.DoesNotContain("\"HomeNameEdited\"", json);
    }

    [Fact]
    public async Task EmptyConfig_GetsADefaultHouse()
    {
        await using var m = Create();
        var house = Assert.Single(m.Houses);
        Assert.Equal("My home", house.Name);
    }

    // ---------------------------------------------------------------- houses

    [Fact]
    public async Task LastHouse_CannotBeDeleted()
    {
        await using var m = Create();
        var only = m.CurrentHouse;
        var ex = Assert.Throws<InvalidOperationException>(() => m.DeleteHouse(only.Id));
        Assert.Contains("only house", ex.Message);
        Assert.Single(m.Houses);
    }

    [Fact]
    public async Task HouseWithDevices_CannotBeDeleted_EmptyOneCan()
    {
        await using var m = Create(new DeviceConfig { Id = "a", Name = "A" });
        var home = m.CurrentHouse;
        var flat = m.AddHouse("Holiday flat");
        m.SetCurrentHouse(home.Id);

        Assert.Throws<InvalidOperationException>(() => m.DeleteHouse(home.Id)); // still has device "a"
        m.DeleteHouse(flat.Id);
        Assert.Single(m.Houses);
        Assert.Equal(home.Id, m.CurrentHouse.Id);
    }

    [Fact]
    public async Task DeletingTheCurrentHouse_SwitchesToAnother_AndDropsItsGroups()
    {
        await using var m = Create();
        var flat = m.AddHouse("Flat");
        m.SetCurrentHouse(flat.Id);
        m.Config.Groups.Add(new GroupConfig { Name = "Flat lights", HouseId = flat.Id });
        m.DeleteHouse(flat.Id);
        Assert.NotEqual(flat.Id, m.CurrentHouse.Id);
        Assert.Empty(m.Config.Groups);
    }

    [Fact]
    public async Task HouseNames_AreUniqueAndRenamesArePinned()
    {
        await using (var m = Create())
        {
            m.AddHouse("Flat");
            Assert.Throws<InvalidOperationException>(() => m.AddHouse(" flat "));
            Assert.Throws<InvalidOperationException>(() => m.RenameHouse(m.CurrentHouse.Id, "FLAT"));
            m.RenameHouse(m.CurrentHouse.Id, "  Casa Sergio ");
        }
        await using var reloaded = Create();
        Assert.Equal("Casa Sergio", reloaded.CurrentHouse.Name);
        Assert.True(reloaded.CurrentHouse.NameEdited);
    }

    // ---------------------------------------------------------------- rooms

    [Fact]
    public async Task Rooms_AreScopedToTheirHouse()
    {
        await using var m = Create(new DeviceConfig { Id = "a", Name = "A", Room = "Kitchen" });
        var home = m.CurrentHouse;
        var flat = m.AddHouse("Flat");
        m.AddRoom(flat.Id, "Kitchen");            // same name in another house is fine
        Assert.Throws<InvalidOperationException>(() => m.AddRoom(home.Id, "kitchen"));

        m.RenameRoom(flat.Id, "Kitchen", "Kitchenette");
        Assert.Equal("Kitchen", m.Config.Devices[0].Room); // the other house's room is untouched
        Assert.Equal(["Kitchen"], m.RoomsOf(home.Id));
        Assert.Equal(["Kitchenette"], m.RoomsOf(flat.Id));

        m.DeleteRoom(home.Id, "Kitchen");
        Assert.Null(m.Config.Devices[0].Room);
        Assert.True(m.Config.Devices[0].RoomEdited);
    }

    [Fact]
    public async Task RenameAndRoom_ArePinnedAndPersisted()
    {
        await using (var m = Create(new DeviceConfig { Id = "matter-1", Name = "SMART MAT A100 RGBW 827 FR E27" },
                                    new DeviceConfig { Id = "st-1", Name = "LDV E14", Room = "Living room" }))
        {
            m.RenameDevice("matter-1", "  Desk lamp ");
            m.SetDeviceRoom("matter-1", "living ROOM"); // joins the existing room, keeps its spelling
            Assert.Equal(["Living room"], m.RoomsOf(m.CurrentHouse.Id));
            Assert.Throws<InvalidOperationException>(() => m.RenameDevice("matter-1", "  "));
        }
        await using var reloaded = Create();
        var d = reloaded.Config.Devices.Single(x => x.Id == "matter-1");
        Assert.Equal(("Desk lamp", "Living room", true, true), (d.Name, d.Room, d.NameEdited, d.RoomEdited));
    }

    [Fact]
    public async Task DevicesInRoom_FollowAssignments()
    {
        await using var m = Create(new DeviceConfig { Id = "a", Name = "A", Room = "Living room" },
                                   new DeviceConfig { Id = "b", Name = "B", Room = "living room " },
                                   new DeviceConfig { Id = "c", Name = "C" });
        var h = m.CurrentHouse.Id;
        Assert.Equal(["a", "b"], m.DevicesInRoom(h, "Living room").Select(d => d.Id).Order());
        m.SetDeviceRoom("c", "Living room");
        m.SetDeviceRoom("a", null);
        Assert.Equal(["b", "c"], m.DevicesInRoom(h, "Living room").Select(d => d.Id).Order());
    }

    // ---------------------------------------------------------------- syncs

    [Fact]
    public void ApplySynced_RespectsUserEdits_ButTheCloudHouseWins()
    {
        var synced = new DeviceConfig { Name = "old", Room = "Hall", HouseId = "h1" };
        DeviceManager.ApplySynced(synced, "From cloud", "Kitchen", "h1");
        Assert.Equal(("From cloud", "Kitchen"), (synced.Name, synced.Room));

        var edited = new DeviceConfig { Name = "My lamp", Room = "Office", NameEdited = true, RoomEdited = true, HouseId = "h1" };
        DeviceManager.ApplySynced(edited, "From cloud", "Kitchen", "h1");
        Assert.Equal(("My lamp", "Office"), (edited.Name, edited.Room));

        // moved to another location in SmartThings: the house follows, the old room no longer applies
        DeviceManager.ApplySynced(edited, "From cloud", "Bedroom", "h2");
        Assert.Equal(("h2", "Bedroom", false), (edited.HouseId, edited.Room, edited.RoomEdited));
        Assert.Equal("My lamp", edited.Name);

        DeviceManager.ApplySynced(synced, " ", null);
        Assert.Equal(("From cloud", "Kitchen"), (synced.Name, synced.Room));
    }

    // ---------------------------------------------------------------- moving devices between houses

    [Fact]
    public async Task MoveRules_FollowTheDeviceSource()
    {
        await using var m = Create(
            new DeviceConfig { Id = "st", Name = "ST bulb", SmartThingsId = "x" },
            new DeviceConfig { Id = "tuya", Name = "Tuya plug", InCloud = true },
            new DeviceConfig { Id = "matter", Name = "Matter bulb", MatterNodeId = "1", Room = "Kitchen" },
            new DeviceConfig { Id = "manual", Name = "Manual plug" });
        var home = m.CurrentHouse.Id;
        Assert.False(m.HouseMoveRuleFor(m.Config.Devices[3]).Allowed); // only one house yet

        var flat = m.AddHouse("Flat");
        Assert.Throws<InvalidOperationException>(() => m.MoveDeviceToHouse("st", flat.Id, confirmedInSmartPlus: true));
        Assert.Throws<InvalidOperationException>(() => m.MoveDeviceToHouse("tuya", flat.Id, confirmedInSmartPlus: true));

        var rule = m.HouseMoveRuleFor(m.Config.Devices.Single(d => d.Id == "matter"));
        Assert.True(rule is { Allowed: true, NeedsSmartPlusConfirmation: true });
        var ex = Assert.Throws<InvalidOperationException>(() => m.MoveDeviceToHouse("matter", flat.Id));
        Assert.Contains("SMART+", ex.Message);

        m.Config.Groups.Add(new GroupConfig { Name = "Kitchen lights", HouseId = home, DeviceIds = ["matter"] });
        m.MoveDeviceToHouse("matter", flat.Id, confirmedInSmartPlus: true);
        var moved = m.Config.Devices.Single(d => d.Id == "matter");
        Assert.Equal((flat.Id, (string?)null), (moved.HouseId, moved.Room));
        Assert.Empty(m.Config.Groups[0].DeviceIds); // left its old house's group

        m.MoveDeviceToHouse("manual", flat.Id);     // free
        Assert.Equal(flat.Id, m.Config.Devices.Single(d => d.Id == "manual").HouseId);
        Assert.Equal(["st", "tuya"], m.DevicesInHouse(home).Select(d => d.Id).Order());
    }

    [Fact]
    public async Task NewDevices_JoinTheSelectedHouse()
    {
        await using var m = Create();
        var flat = m.AddHouse("Flat");
        m.SetCurrentHouse(flat.Id);
        m.AddOrUpdate(new DeviceConfig { Id = "new", Name = "New plug" });
        Assert.Equal(flat.Id, m.Config.Devices.Single().HouseId);
    }
}
