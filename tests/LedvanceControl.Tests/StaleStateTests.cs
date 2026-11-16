using System.Net;
using System.Text.Json.Nodes;
using LedvanceControl.Core.Cloud;
using LedvanceControl.Core.Model;
using LedvanceControl.Core.Services;
using Xunit;

namespace LedvanceControl.Tests;

/// <summary>
/// A cloud device confirms slowly: status reads right after a change still carry the old value. Those
/// must not flip the control back (the "had to click twice" bug), and a change the device lost is resent.
/// </summary>
public sealed class StaleStateTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "stale-test-" + Guid.NewGuid().ToString("N"), "config.json");
    private readonly FakeSmartThings _cloud = new();

    public StaleStateTests()
    {
        DeviceRuntime.CloudHold = TimeSpan.FromMilliseconds(800);
        DeviceRuntime.VerifyAfter = TimeSpan.FromMilliseconds(300);
    }

    public void Dispose()
    {
        DeviceRuntime.CloudHold = TimeSpan.FromSeconds(5);
        DeviceRuntime.VerifyAfter = TimeSpan.FromSeconds(4);
        try { Directory.Delete(Path.GetDirectoryName(_path)!, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private async Task<(DeviceManager, DeviceRuntime)> CreateAsync()
    {
        var store = TestStores.Create(_path);
        var cfg = store.Load();
        cfg.Devices.Add(new DeviceConfig
        {
            Id = "e14", Name = "LDV E14", Category = "dj", SmartThingsId = "st-e14",
            Schema = SmartThingsMapping.BuildSchema(["switch", "switchLevel", "colorTemperature"]),
        });
        store.Save(cfg);
        var m = new DeviceManager(store);
        m.UseSmartThings(new SmartThingsClient("id", "secret", "refresh", _cloud));
        var rt = m.Find("e14")!;
        await rt.RefreshAsync();
        return (m, rt);
    }

    private static long Level(DeviceRuntime rt) => (long)rt.Get("bright_value_v2")!;

    [Fact]
    public async Task StaleStatus_RightAfterAChange_DoesNotFlipItBack()
    {
        _cloud.Applies = false; // the cloud keeps reporting the old values for now
        var (m, rt) = await CreateAsync();
        await using var _ = m;
        Assert.Equal(5, Level(rt));

        await rt.SetAsync("bright_value_v2", 50L);
        Assert.Equal(50, Level(rt));                 // shown at once
        await rt.RefreshAsync();                     // stale report: still 5
        Assert.Equal(50, Level(rt));                 // ...ignored, the slider stays at 50

        _cloud.Applies = true; _cloud.Level = 50;    // the device catches up
        await rt.RefreshAsync();
        Assert.Equal(50, Level(rt));
    }

    [Fact]
    public async Task WhenTheDeviceReallyDidNotChange_TheRealValueShowsAfterTheHold()
    {
        _cloud.Applies = false;
        var (m, rt) = await CreateAsync();
        await using var _ = m;
        _cloud.DropCommands = true; // and resends are lost too: the device stays at 5

        await rt.SetAsync("bright_value_v2", 50L);
        await Task.Delay(DeviceRuntime.CloudHold + TimeSpan.FromMilliseconds(200));
        await rt.RefreshAsync();
        Assert.Equal(5, Level(rt)); // no permanent lie: after the hold, the reported value wins
    }

    [Fact]
    public async Task LostChange_IsVerifiedAndSentAgain()
    {
        var (m, rt) = await CreateAsync();
        await using var _ = m;
        var resent = new TaskCompletionSource<int>();
        rt.Resent += (_, n) => resent.TrySetResult(n);

        _cloud.DropNext = 1; // the cloud accepts the first command but it never reaches the bulb
        await rt.SetAsync("bright_value_v2", 50L);
        Assert.Equal(1, await resent.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(50, _cloud.Level); // the resend got through
    }

    [Fact]
    public async Task OnlyTheLastOfQuickChanges_IsVerified()
    {
        var (m, rt) = await CreateAsync();
        await using var _ = m;
        await rt.SetAsync("bright_value_v2", 20L);
        await rt.SetAsync("bright_value_v2", 35L);
        await rt.SetAsync("bright_value_v2", 50L);
        await Task.Delay(DeviceRuntime.VerifyAfter + TimeSpan.FromMilliseconds(500));
        Assert.Equal(1, _cloud.StatusReads(after: "verify")); // one check, for the final value
        Assert.Equal(50, _cloud.Level);
    }

    [Fact]
    public void NotApplied_ComparesPowerAndBrightness_IgnoresColour()
    {
        var sent = SmartThingsMapping.CodesToCommands(new Dictionary<string, object?>
        {
            ["switch_led"] = true, ["bright_value_v2"] = 50L, ["temp_value_v2"] = 30L,
        });
        var status = JsonNode.Parse("""{"switch":{"switch":{"value":"on"}},"switchLevel":{"level":{"value":12}},"colorTemperature":{"colorTemperature":{"value":2000}}}""")!.AsObject();
        var missing = SmartThingsMapping.NotApplied(sent, status);
        Assert.Single(missing);
        Assert.Equal("setLevel", missing[0]!["command"]!.ToString());

        status["switchLevel"]!["level"]!["value"] = 51; // within ±1: applied
        Assert.Empty(SmartThingsMapping.NotApplied(sent, status));
    }

    /// <summary>SmartThings REST API stand-in for one light.</summary>
    private sealed class FakeSmartThings : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly List<string> _log = [];
        public bool On = true;
        public int Level = 5;
        /// <summary>False: commands are accepted but status keeps the old values (slow cloud).</summary>
        public bool Applies = true;
        public bool DropCommands;
        public int DropNext;

        public int StatusReads(string after)
        {
            lock (_gate)
            {
                var i = _log.LastIndexOf("command");
                return i < 0 ? 0 : _log.Skip(i + 1).Count(e => e == "status");
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            string body;
            if (path.Contains("token"))
            {
                body = """{"access_token":"a","refresh_token":"r","expires_in":86400}""";
            }
            else if (path.EndsWith("/commands"))
            {
                var json = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
                lock (_gate)
                {
                    _log.Add("command");
                    var drop = DropCommands || DropNext > 0;
                    if (DropNext > 0) DropNext--;
                    if (!drop && Applies)
                        foreach (var c in json["commands"]!.AsArray())
                        {
                            if (c!["capability"]!.ToString() == "switch") On = c["command"]!.ToString() == "on";
                            if (c["command"]!.ToString() == "setLevel") Level = c["arguments"]![0]!.GetValue<int>();
                        }
                }
                body = "{}";
            }
            else if (path.EndsWith("/status"))
            {
                lock (_gate)
                {
                    _log.Add("status");
                    body = new JsonObject
                    {
                        ["components"] = new JsonObject
                        {
                            ["main"] = new JsonObject
                            {
                                ["switch"] = new JsonObject { ["switch"] = new JsonObject { ["value"] = On ? "on" : "off" } },
                                ["switchLevel"] = new JsonObject { ["level"] = new JsonObject { ["value"] = Level } },
                                ["colorTemperature"] = new JsonObject { ["colorTemperature"] = new JsonObject { ["value"] = 2700 } },
                            },
                        },
                    }.ToJsonString();
                }
            }
            else if (path.EndsWith("/health"))
            {
                body = """{"state":"ONLINE"}""";
            }
            else
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }
}
