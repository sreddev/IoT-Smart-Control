using LedvanceControl.Core.Model;
using LedvanceControl.Core.Services;
using Xunit;

namespace LedvanceControl.Tests;

public sealed class RefreshTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "refresh-test-" + Guid.NewGuid().ToString("N"), "config.json");

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_path)!, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>The UI shows a per-device spinner from IsRefreshing; it must be raised and always cleared.</summary>
    [Fact]
    public async Task Refresh_RaisesRefreshingThenClearsIt_EvenWhenTheDeviceIsUnreachable()
    {
        var store = TestStores.Create(_path);
        var cfg = store.Load();
        cfg.Devices.Add(new DeviceConfig { Id = "lonely", Name = "No IP, no cloud" }); // nothing to reach it with
        store.Save(cfg);
        await using var m = new DeviceManager(store);
        var rt = m.Find("lonely")!;

        var seen = new List<bool>();
        rt.Changed += d => seen.Add(d.IsRefreshing);
        await rt.RefreshAsync();

        Assert.True(seen.Count >= 2);
        Assert.True(seen[0]);                 // first notification: refreshing
        Assert.False(seen[^1]);               // last notification: done
        Assert.False(rt.IsRefreshing);
        Assert.False(rt.Online);
        Assert.NotNull(rt.LastError);
    }
}
