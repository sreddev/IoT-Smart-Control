using LedvanceControl.Core.Model;
using LedvanceControl.Core.Services;
using Xunit;

namespace LedvanceControl.Tests;

public sealed class RoomTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "rooms-test-" + Guid.NewGuid().ToString("N"), "config.json");

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_path)!, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private async Task<DeviceManager> CreateAsync(params DeviceConfig[] devices)
    {
        var store = new ConfigStore(_path);
        if (devices.Length > 0)
        {
            var cfg = store.Load();
            cfg.Devices.AddRange(devices);
            store.Save(cfg);
        }
        await Task.CompletedTask;
        return new DeviceManager(store);
    }

    [Fact]
    public void ApplySynced_RespectsUserEdits()
    {
        var synced = new DeviceConfig { Name = "old", Room = "Hall" };
        DeviceManager.ApplySynced(synced, "From cloud", "Kitchen");
        Assert.Equal(("From cloud", "Kitchen"), (synced.Name, synced.Room));

        var edited = new DeviceConfig { Name = "My lamp", Room = "Office", NameEdited = true, RoomEdited = true };
        DeviceManager.ApplySynced(edited, "From cloud", "Kitchen");
        Assert.Equal(("My lamp", "Office"), (edited.Name, edited.Room));

        // blank sync values never wipe what is there
        DeviceManager.ApplySynced(synced, " ", null);
        Assert.Equal(("From cloud", "Kitchen"), (synced.Name, synced.Room));
    }

    [Fact]
    public async Task RenameAndRoom_ArePinnedAndPersisted()
    {
        await using (var m = await CreateAsync(new DeviceConfig { Id = "matter-1", Name = "SMART MAT A100 RGBW 827 FR E27" },
                                               new DeviceConfig { Id = "st-1", Name = "LDV E14", Room = "Living room" }))
        {
            var changes = 0;
            m.ListChanged += () => changes++;
            m.RenameDevice("matter-1", "  Desk lamp ");
            m.SetDeviceRoom("matter-1", "living ROOM"); // joins the existing room, keeps its spelling
            Assert.Equal(2, changes);
            Assert.Equal(["Living room"], m.KnownRooms);
        }

        await using var reloaded = await CreateAsync();
        var d = reloaded.Config.Devices.Single(x => x.Id == "matter-1");
        Assert.Equal("Desk lamp", d.Name);
        Assert.Equal("Living room", d.Room);
        Assert.True(d.NameEdited);
        Assert.True(d.RoomEdited);
    }

    [Fact]
    public async Task NewRoomFromDevice_IsAddedToTheList()
    {
        await using var m = await CreateAsync(new DeviceConfig { Id = "a", Name = "A" });
        m.SetDeviceRoom("a", "Bedroom");
        Assert.Contains("Bedroom", m.Config.Rooms);
        m.SetDeviceRoom("a", "");
        Assert.Null(m.Config.Devices[0].Room);
        Assert.Contains("Bedroom", m.KnownRooms); // empty rooms stay available
    }

    [Fact]
    public async Task RoomCrud_MovesDevices()
    {
        await using var m = await CreateAsync(new DeviceConfig { Id = "a", Name = "A", Room = "Living room" },
                                              new DeviceConfig { Id = "b", Name = "B", Room = "Living room" });
        m.AddRoom("Kitchen");
        Assert.Equal(["Kitchen", "Living room"], m.KnownRooms);
        Assert.Throws<InvalidOperationException>(() => m.AddRoom("kitchen"));
        Assert.Throws<InvalidOperationException>(() => m.RenameRoom("Living room", "KITCHEN"));

        m.RenameRoom("Living room", "Lounge");
        Assert.All(m.Config.Devices, d => Assert.Equal("Lounge", d.Room));
        Assert.All(m.Config.Devices, d => Assert.True(d.RoomEdited));
        Assert.Equal(["Kitchen", "Lounge"], m.KnownRooms);

        m.DeleteRoom("Lounge");
        Assert.All(m.Config.Devices, d => Assert.Null(d.Room));
        Assert.Equal(["Kitchen"], m.KnownRooms);
    }

    [Fact]
    public async Task Rename_RejectsEmptyName()
    {
        await using var m = await CreateAsync(new DeviceConfig { Id = "a", Name = "A" });
        Assert.Throws<InvalidOperationException>(() => m.RenameDevice("a", "   "));
        Assert.Equal("A", m.Config.Devices[0].Name);
    }
}
