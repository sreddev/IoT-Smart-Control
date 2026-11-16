using System.Text.Json.Nodes;
using LedvanceControl.Core.Lights;
using LedvanceControl.Core.Local;
using LedvanceControl.Core.Model;
using Xunit;

namespace LedvanceControl.Tests;

public class MatterMappingTests
{
    private static readonly MatterCapabilities Rgbw = new() { Level = true, Hs = true, Xy = true, Ct = true, CtMinKelvin = 2700, CtMaxKelvin = 6500 };

    [Fact]
    public void Schema_ForRgbwBulb_DrivesTheLightProfile()
    {
        var schema = MatterMapping.BuildSchema(Rgbw);
        var light = LightProfile.From(schema);

        Assert.NotNull(light);
        Assert.True(light!.HasBrightness);
        Assert.True(light.HasColour);
        Assert.True(light.HasTemp);
        Assert.False(light.HasScenes); // Tuya scene strings mean nothing to a Matter bulb
        Assert.Equal(["white", "colour"], light.Modes);
        // 0 % = warm end, 100 % = cold end, expressed directly in Kelvin
        Assert.Equal(2700, light.TempToRaw(0));
        Assert.Equal(6500, light.TempToRaw(100));
        Assert.Equal("dj", MatterMapping.GuessCategory(Rgbw));
    }

    [Fact]
    public void Schema_ForOnOffOnlyDevice_IsNotALight()
    {
        var caps = new MatterCapabilities();
        Assert.Null(LightProfile.From(MatterMapping.BuildSchema(caps)));
        Assert.Equal("cz", MatterMapping.GuessCategory(caps));
    }

    [Fact]
    public void WhiteCommand_FromLightProfile_BecomesLevelAndKelvin()
    {
        var light = LightProfile.From(MatterMapping.BuildSchema(Rgbw))!;
        var cmd = MatterMapping.CodesToCommand(light.SetWhite(40, 0));

        Assert.True(cmd["on"]!.GetValue<bool>());
        Assert.Equal(40, cmd["level"]!.GetValue<int>());
        Assert.Equal(2700, cmd["colorTempKelvin"]!.GetValue<int>());
        Assert.False(cmd.ContainsKey("hue"));
    }

    [Fact]
    public void ColourCommand_FromLightProfile_BecomesHueSaturationAndLevel()
    {
        var light = LightProfile.From(MatterMapping.BuildSchema(Rgbw))!;
        var cmd = MatterMapping.CodesToCommand(light.SetColour(new HsvColor(120, 1000, 500)));

        Assert.True(cmd["on"]!.GetValue<bool>());
        Assert.Equal(120, cmd["hue"]!.GetValue<int>());
        Assert.Equal(100.0, cmd["saturation"]!.GetValue<double>());
        Assert.Equal(50, cmd["level"]!.GetValue<int>()); // value channel carries brightness
        Assert.False(cmd.ContainsKey("colorTempKelvin"));
    }

    [Fact]
    public void PowerOff_IsJustOff()
    {
        var cmd = MatterMapping.CodesToCommand(new Dictionary<string, object?> { ["switch_led"] = false });
        Assert.Single(cmd);
        Assert.False(cmd["on"]!.GetValue<bool>());
    }

    [Fact]
    public void CapturedScene_UsesWorkModeToPickColourOrWhite()
    {
        var both = new Dictionary<string, object?>
        {
            ["switch_led"] = true,
            ["work_mode"] = "white",
            ["bright_value_v2"] = 70L,
            ["temp_value_v2"] = 4000L,
            ["colour_data_v2"] = new HsvColor(240, 1000, 1000).ToV2Hex(),
        };
        var white = MatterMapping.CodesToCommand(both);
        Assert.Equal(4000, white["colorTempKelvin"]!.GetValue<int>());
        Assert.False(white.ContainsKey("hue"));
        Assert.Equal(70, white["level"]!.GetValue<int>());

        both["work_mode"] = "colour";
        var colour = MatterMapping.CodesToCommand(both);
        Assert.Equal(240, colour["hue"]!.GetValue<int>());
        Assert.False(colour.ContainsKey("colorTempKelvin"));
        Assert.Equal(70, colour["level"]!.GetValue<int>()); // explicit brightness wins over the colour's value
    }

    [Fact]
    public void State_MapsToInternalCodes()
    {
        var schema = MatterMapping.BuildSchema(Rgbw);
        var codes = MatterMapping.StateToCodes(new MatterLightState
        {
            On = true, Level = 50, Hue = 120, Saturation = 100, ColorTempKelvin = 9000, ColorMode = "hs",
        }, schema);

        Assert.Equal(true, codes["switch_led"]);
        Assert.Equal(50L, codes["bright_value_v2"]);
        Assert.Equal(6500L, codes["temp_value_v2"]); // clamped to the bulb's range
        Assert.Equal("colour", codes["work_mode"]);
        Assert.True(HsvColor.TryParse((string)codes["colour_data_v2"]!, out var c));
        Assert.Equal(new HsvColor(120, 1000, 500), c);
    }

    [Fact]
    public void State_InCtMode_IsWhite()
    {
        var codes = MatterMapping.StateToCodes(new MatterLightState { On = false, ColorMode = "ct" }, MatterMapping.BuildSchema(Rgbw));
        Assert.Equal(false, codes["switch_led"]);
        Assert.Equal("white", codes["work_mode"]);
    }
}

/// <summary>Exercises the stdio protocol of <see cref="MatterBridgeClient"/> against a scripted fake bridge.</summary>
public sealed class MatterBridgeClientTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "matter-bridge-test-" + Guid.NewGuid().ToString("N"));

    public MatterBridgeClientTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string? Node => MatterBridgeOptions.Locate(AppContext.BaseDirectory, Path.GetTempPath())?.NodeExecutable
        ?? FindOnPath();

    /// <summary>src/LedvanceControl.MatterBridge/dist/bridge.mjs, found by walking up from the test output.</summary>
    private static string? FindBundle()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "LedvanceControl.MatterBridge", "dist", "bridge.mjs");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string? FindOnPath() =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
        .Select(d => Path.Combine(d.Trim('"'), "node.exe")).FirstOrDefault(File.Exists);

    private const string FakeBridge = """
        import { createInterface } from "node:readline";
        const send = o => process.stdout.write(JSON.stringify(o) + "\n");
        process.stderr.write("fake bridge starting\n");
        send({ event: "ready", nodes: ["7"] });
        createInterface({ input: process.stdin }).on("line", line => {
            const r = JSON.parse(line);
            switch (r.cmd) {
                case "set":
                    send({ event: "state", nodeId: "7", state: { on: r.on, level: r.level } });
                    send({ id: r.id, ok: true, result: { on: r.on, level: r.level } });
                    break;
                case "nodes":
                    send({ id: r.id, ok: true, result: [{ nodeId: "7", name: "Lamp", connected: true,
                        capabilities: { onOff: true, level: true, hs: true, xy: false, ct: true, ctMinKelvin: 2200, ctMaxKelvin: 6500 } }] });
                    break;
                case "crash":
                    process.stderr.write("Error: simulated crash\n");
                    process.exit(3);
                default:
                    send({ id: r.id, ok: false, error: "nope: " + r.cmd });
            }
        });
        """;

    private MatterBridgeClient? CreateFake()
    {
        if (Node is not { } node) return null; // Node.js not installed on this machine: nothing to test
        var script = Path.Combine(_dir, "fake.mjs");
        File.WriteAllText(script, FakeBridge);
        return new MatterBridgeClient(new MatterBridgeOptions(node, script, Path.Combine(_dir, "storage")));
    }

    [Fact]
    public async Task Request_Response_And_PushedState()
    {
        await using var client = CreateFake();
        if (client == null) return;

        var pushed = new TaskCompletionSource<(string, MatterLightState)>();
        client.StateReceived += (id, s) => pushed.TrySetResult((id, s));

        var result = await client.SetAsync("7", new JsonObject { ["on"] = true, ["level"] = 42 });
        Assert.True(result.On);
        Assert.Equal(42, result.Level);

        var (nodeId, state) = await pushed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("7", nodeId);
        Assert.Equal(42, state.Level);

        var nodes = await client.GetNodesAsync();
        var lamp = Assert.Single(nodes);
        Assert.Equal("Lamp", lamp.Name);
        Assert.Equal(2200, lamp.Capabilities!.CtMinKelvin);
    }

    [Fact]
    public async Task BridgeErrors_SurfaceAsMatterException()
    {
        await using var client = CreateFake();
        if (client == null) return;
        var ex = await Assert.ThrowsAsync<MatterException>(() => client.RemoveAsync("7"));
        Assert.Contains("nope: remove", ex.Message);
    }

    [Fact]
    public async Task Crash_FailsPendingRequest_ThenRestartsOnNextRequest()
    {
        await using var client = CreateFake();
        if (client == null) return;

        var ex = await Assert.ThrowsAsync<MatterException>(() => client.RequestAsync("crash", null, TimeSpan.FromSeconds(10)));
        Assert.Contains("exited", ex.Message);
        Assert.Contains("simulated crash", ex.Message);

        // next request transparently starts a fresh bridge process
        var nodes = await client.GetNodesAsync();
        Assert.Single(nodes);
    }

    /// <summary>Starts the real bundled bridge (matter.js) with an empty fabric.</summary>
    [Fact]
    public async Task RealBridge_StartsWithEmptyFabric()
    {
        if (Node is not { } node || FindBundle() is not { } bundle) return; // Node.js missing or bridge not built
        var options = new MatterBridgeOptions(node, bundle, Path.Combine(_dir, "real"));
        await using var client = new MatterBridgeClient(options);

        var ping = await client.RequestAsync("ping", null, TimeSpan.FromSeconds(60));
        Assert.Equal(1, ping!["version"]!.GetValue<int>());
        Assert.Empty(await client.GetNodesAsync());
        var bad = await Assert.ThrowsAsync<MatterException>(() => client.CommissionAsync("12"));
        Assert.Contains("11 or 21 digits", bad.Message);
        Assert.True(Directory.EnumerateFiles(options.StorageDirectory, "*", SearchOption.AllDirectories).Any(), "fabric was persisted");
    }
}
