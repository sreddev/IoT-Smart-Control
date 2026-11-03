using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LedvanceControl.Core.Cloud;
using LedvanceControl.Core.Lights;
using LedvanceControl.Core.Local;
using LedvanceControl.Core.Model;
using LedvanceControl.Core.Services;

namespace LedvanceControl.Tests;

public class ProtocolTests
{
    private const string Key = "0123456789abcdef";

    [Fact]
    public void Crc32_MatchesReferenceValue()
    {
        Assert.Equal(0xCBF43926u, Crc32.Compute("123456789"u8));
    }

    [Fact]
    public void Frame55AA_RoundTripsWithCrc()
    {
        var payload = Encoding.UTF8.GetBytes("{\"a\":1}");
        var frame = TuyaCodec.Pack55AA(7, TuyaCommand.Control, payload, null);
        Assert.Equal(frame.Length, TuyaCodec.FrameLength(frame));
        var msg = TuyaCodec.Unpack55AA(frame, null);
        Assert.Equal(7u, msg.Seq);
        Assert.Equal(TuyaCommand.Control, msg.Command);
        Assert.Equal(payload, msg.Payload);
    }

    [Fact]
    public void Frame55AA_DetectsCorruption()
    {
        var frame = TuyaCodec.Pack55AA(1, TuyaCommand.DpQuery, "{}"u8.ToArray(), null);
        frame[17] ^= 0xFF;
        Assert.Throws<InvalidDataException>(() => TuyaCodec.Unpack55AA(frame, null));
    }

    [Fact]
    public void Frame6699_RoundTripsWithGcm()
    {
        var key = Encoding.ASCII.GetBytes(Key);
        var frame = TuyaCodec.Pack6699(3, TuyaCommand.DpQueryNew, "{\"x\":true}"u8.ToArray(), key);
        Assert.Equal(frame.Length, TuyaCodec.FrameLength(frame));
        var msg = TuyaCodec.Unpack6699(frame, key);
        Assert.Equal("{\"x\":true}", msg.PayloadText);
    }

    [Fact]
    public void V33_Payload_HasVersionHeaderOnlyForControl()
    {
        var key = Encoding.ASCII.GetBytes(Key);
        var ctl = TuyaCodec.EncryptPayload55AA(TuyaCommand.Control, "{}"u8.ToArray(), TuyaVersion.V33, key);
        var qry = TuyaCodec.EncryptPayload55AA(TuyaCommand.DpQuery, "{}"u8.ToArray(), TuyaVersion.V33, key);
        Assert.StartsWith("3.3", Encoding.ASCII.GetString(ctl, 0, 3));
        Assert.Equal(16, qry.Length);
        Assert.Equal("{}", Encoding.ASCII.GetString(TuyaCodec.DecryptPayload55AA(ctl, TuyaVersion.V33, key)));
    }

    [Fact]
    public void Discovery_DecodesEncryptedBroadcast()
    {
        var json = "{\"ip\":\"192.168.1.50\",\"gwId\":\"bf1234567890abcdef\",\"active\":2,\"encrypt\":true,\"productKey\":\"abc\",\"version\":\"3.3\"}";
        var enc = TuyaCodec.EcbEncrypt(TuyaCodec.UdpKey, Encoding.UTF8.GetBytes(json));
        var frame = TuyaCodec.Pack55AA(0, TuyaCommand.UdpNew, [0, 0, 0, 0, .. enc], null);
        var dev = TuyaDiscovery.TryDecode(frame, "10.0.0.1");
        Assert.NotNull(dev);
        Assert.Equal("bf1234567890abcdef", dev!.Id);
        Assert.Equal("192.168.1.50", dev.Ip);
        Assert.Equal("3.3", dev.Version);
    }

    [Fact]
    public void Discovery_Decodes35Broadcast()
    {
        var json = "{\"ip\":\"192.168.1.51\",\"gwId\":\"bf35\",\"version\":\"3.5\"}";
        var frame = TuyaCodec.Pack6699(0, TuyaCommand.BroadcastLpv34, [0, 0, 0, 0, .. Encoding.UTF8.GetBytes(json)], TuyaCodec.UdpKey);
        var dev = TuyaDiscovery.TryDecode(frame, "10.0.0.1");
        Assert.Equal("3.5", dev!.Version);
    }

    [Theory]
    [InlineData(TuyaVersion.V33)]
    [InlineData(TuyaVersion.V34)]
    [InlineData(TuyaVersion.V35)]
    public async Task LocalClient_QueriesAndControlsFakeDevice(TuyaVersion version)
    {
        await using var device = new FakeTuyaDevice(Key, version);
        await using var client = new TuyaLocalClient("bf0000000000000000", "127.0.0.1", Key, version, device.Port);

        var dps = await client.QueryAsync();
        Assert.Equal(500, dps["22"].GetInt32());
        Assert.False(dps["20"].GetBoolean());

        var pushed = new TaskCompletionSource<IReadOnlyDictionary<string, JsonElement>>();
        client.DpsReceived += d => pushed.TrySetResult(d);
        await client.SetDpsAsync(new Dictionary<string, object?> { ["20"] = true, ["22"] = 800 });
        var update = await pushed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(update["20"].GetBoolean());

        var after = await client.QueryAsync();
        Assert.Equal(800, after["22"].GetInt32());
    }

    [Fact]
    public async Task LocalClient_WrongKeyFailsNegotiation()
    {
        await using var device = new FakeTuyaDevice(Key, TuyaVersion.V34);
        await using var client = new TuyaLocalClient("bf00", "127.0.0.1", "ffffffffffffffff", TuyaVersion.V34, device.Port)
        {
            Timeout = TimeSpan.FromSeconds(2),
        };
        await Assert.ThrowsAnyAsync<Exception>(() => client.QueryAsync());
    }
}

public class LightAndCloudTests
{
    [Fact]
    public void HsvColor_V2HexRoundTrip()
    {
        var c = new HsvColor(240, 1000, 500);
        Assert.Equal("00f003e801f4", c.ToV2Hex());
        Assert.True(HsvColor.TryParse("00f003e801f4", out var p));
        Assert.Equal(c, p);
    }

    [Fact]
    public void HsvColor_ParsesCloudJsonAndV1()
    {
        Assert.True(HsvColor.TryParse("{\"h\":120,\"s\":1000,\"v\":1000}", out var j));
        Assert.Equal(new HsvColor(120, 1000, 1000), j);
        var v1 = new HsvColor(0, 1000, 1000).ToV1Hex();
        Assert.Equal("ff00000000ffff", v1);
        Assert.True(HsvColor.TryParse(v1, out var p));
        Assert.Equal(0, p.H);
    }

    [Fact]
    public void HsvColor_RgbConversion()
    {
        Assert.Equal(((byte)0, (byte)0, (byte)255), new HsvColor(240, 1000, 1000).ToRgb());
        Assert.Equal(new HsvColor(120, 1000, 1000), HsvColor.FromRgb(0, 255, 0));
    }

    [Fact]
    public void LightProfile_ScalesV2Brightness()
    {
        var p = LightProfile.From(DefaultSchemas.LightV2())!;
        Assert.Equal(1000, p.BrightnessToRaw(100));
        Assert.Equal(20, p.BrightnessToRaw(0)); // clamped to 1 %
        Assert.Equal("white", p.SetWhite(50, 50)["work_mode"]);
        Assert.Equal(505L, p.SetWhite(50, null)["bright_value_v2"]);
        Assert.True(p.HasScenes);
    }

    [Fact]
    public void DefaultSchemas_GuessFromObservedDps()
    {
        var observed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("{\"20\":true,\"21\":\"white\",\"22\":1000,\"23\":0,\"24\":\"000003e803e8\",\"99\":5}")!;
        var schema = DefaultSchemas.Guess(null, observed);
        Assert.Contains(schema, s => s.Code == "switch_led");
        Assert.DoesNotContain(schema, s => s.Code == "scene_data_v2");
        Assert.Contains(schema, s => s.Id == "99" && s.Type == DpType.Value);
        Assert.NotNull(LightProfile.From(schema));
    }

    [Fact]
    public void CloudSignature_CanonicalisesQueryAndUsesHmacSha256()
    {
        Assert.Equal("/v1.0/x?a=1&b=2", TuyaCloudClient.CanonicalUrl("/v1.0/x?b=2&a=1"));
        // computed independently: HMAC-SHA256("secret", "id" + "token" + "1000" + "n" + "GET\n<sha256('')>\n\n/v1.0/x")
        var expectedInput = "idtoken1000nGET\ne3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855\n\n/v1.0/x";
        var expected = Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData("secret"u8.ToArray(), Encoding.UTF8.GetBytes(expectedInput)));
        Assert.Equal(expected, TuyaCloudClient.Sign("id", "secret", "token", "1000", "n", "GET", "", "/v1.0/x"));
    }

    [Fact]
    public void FromJson_KeepsIntegersAsLong()
    {
        using var doc = JsonDocument.Parse("[500, 1.5, true, \"x\"]");
        var a = doc.RootElement;
        Assert.IsType<long>(DeviceRuntime.FromJson(a[0]));
        Assert.IsType<double>(DeviceRuntime.FromJson(a[1]));
        Assert.IsType<bool>(DeviceRuntime.FromJson(a[2]));
        Assert.IsType<string>(DeviceRuntime.FromJson(a[3]));
    }

    [Fact]
    public void TinyTuyaImport_ReadsKeysAndMapping()
    {
        var json = """
        [{"name":"Desk lamp","id":"bf11","key":"abcdefghijklmnop","ip":"192.168.1.9","version":"3.4","category":"dj",
          "mapping":{"20":{"code":"switch_led","type":"Boolean","values":{}},
                     "22":{"code":"bright_value_v2","type":"Integer","values":{"min":10,"max":1000,"scale":0,"step":1}},
                     "24":{"code":"colour_data_v2","type":"Json","values":{}}}}]
        """;
        var dev = Assert.Single(TinyTuyaImporter.Import(json));
        Assert.Equal("abcdefghijklmnop", dev.LocalKey);
        Assert.Equal("3.4", dev.Version);
        Assert.Equal(3, dev.Schema.Count);
        Assert.Equal(1000, dev.Schema.Single(s => s.Code == "bright_value_v2").Max);
        Assert.NotNull(LightProfile.From(dev.Schema));
    }

    [Fact]
    public void SmartThings_GreenMapsToHue33()
    {
        // internal green is Tuya v2 hex for HSV(120,1000,1000)
        var hex = new HsvColor(120, 1000, 1000).ToV2Hex();
        var cmds = SmartThingsMapping.CodesToCommands(new Dictionary<string, object?> { ["colour_data_v2"] = hex });
        var setColor = cmds.First(c => c!["capability"]!.GetValue<string>() == "colorControl");
        var arg = setColor!["arguments"]![0]!;
        Assert.Equal(33.3, arg["hue"]!.GetValue<double>(), 1);       // 120 / 3.6
        Assert.Equal(100.0, arg["saturation"]!.GetValue<double>(), 1);
    }

    [Fact]
    public void SmartThings_SwitchAndLevelCommands()
    {
        var cmds = SmartThingsMapping.CodesToCommands(new Dictionary<string, object?> { ["switch_led"] = true, ["bright_value_v2"] = 50L });
        Assert.Contains(cmds, c => c!["capability"]!.GetValue<string>() == "switch" && c["command"]!.GetValue<string>() == "on");
        var level = cmds.First(c => c!["capability"]!.GetValue<string>() == "switchLevel");
        Assert.Equal(50, level!["arguments"]![0]!.GetValue<int>());
    }

    [Fact]
    public void SmartThings_StatusParsesIntoCodes()
    {
        var main = (JsonObject)JsonNode.Parse("""
        {"switch":{"switch":{"value":"on"}},
         "switchLevel":{"level":{"value":60}},
         "colorControl":{"hue":{"value":33.3},"saturation":{"value":100}},
         "colorTemperature":{"colorTemperature":{"value":4600}}}
        """)!;
        var codes = SmartThingsMapping.StatusToCodes(main);
        Assert.Equal(true, codes["switch_led"]);
        Assert.Equal(60L, codes["bright_value_v2"]);
        Assert.True(HsvColor.TryParse((string)codes["colour_data_v2"]!, out var c));
        Assert.InRange(c.H, 118, 122); // ~green
        Assert.Equal(50L, codes["temp_value_v2"]); // 4600K is mid of 2700..6500
    }

    [Fact]
    public void SmartThings_SchemaMakesAColourLight()
    {
        var schema = SmartThingsMapping.BuildSchema(["switch", "switchLevel", "colorControl", "colorTemperature"]);
        var profile = LightProfile.From(schema);
        Assert.NotNull(profile);
        Assert.True(profile!.HasColour);
        Assert.True(profile.HasTemp);
    }

    [Fact]
    public void SmartThings_KelvinRoundTrip()
    {
        Assert.Equal(2700, SmartThingsMapping.PercentToKelvin(0), 0);
        Assert.Equal(6500, SmartThingsMapping.PercentToKelvin(100), 0);
        Assert.Equal(50, SmartThingsMapping.KelvinToPercent(SmartThingsMapping.PercentToKelvin(50)), 1);
    }

    [Fact]
    public void Scheduler_IsDueOncePerDay()
    {
        var e = new ScheduleEntry { Time = new TimeOnly(7, 30), Days = [DayOfWeek.Monday] };
        var monday = new DateTime(2026, 11, 2);
        Assert.True(Scheduler.IsDue(e, monday.AddHours(7.49), monday.AddHours(7.51)));
        Assert.False(Scheduler.IsDue(e, monday.AddHours(7.51), monday.AddHours(7.6)));
        e.LastRun = monday.AddHours(7.5);
        Assert.False(Scheduler.IsDue(e, monday.AddHours(7.49), monday.AddHours(7.51)));
        Assert.False(Scheduler.IsDue(new ScheduleEntry { Time = new TimeOnly(7, 30), Days = [DayOfWeek.Tuesday] }, monday.AddHours(7), monday.AddHours(8)));
    }

    [Fact]
    public void ConfigStore_ProtectsSecrets()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ldv-test-{Guid.NewGuid():N}.json");
        try
        {
            var store = new ConfigStore(path);
            var cfg = new AppConfig();
            cfg.Cloud.ClientSecret = "supersecret";
            cfg.Devices.Add(new DeviceConfig { Id = "a", LocalKey = "0123456789abcdef" });
            store.Save(cfg);
            var text = File.ReadAllText(path);
            Assert.DoesNotContain("supersecret", text);
            Assert.DoesNotContain("0123456789abcdef", text);
            var loaded = store.Load();
            Assert.Equal("supersecret", loaded.Cloud.ClientSecret);
            Assert.Equal("0123456789abcdef", loaded.Devices[0].LocalKey);
            Assert.Equal("supersecret", cfg.Cloud.ClientSecret);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
