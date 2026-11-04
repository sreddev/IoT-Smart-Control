using System.Text.Json;
using System.Text.Json.Serialization;

namespace LedvanceControl.Core.Model;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DpType { Bool, Value, Enum, String, Json, Raw, Bitmap }

/// <summary>Description of one data point ("DP") of a device: id, code, type and range.</summary>
public sealed class DpSchema
{
    public string Id { get; set; } = "";
    public string Code { get; set; } = "";
    public string? Name { get; set; }
    public DpType Type { get; set; } = DpType.Raw;
    /// <summary>rw, ro or wr.</summary>
    public string Mode { get; set; } = "rw";
    public long Min { get; set; }
    public long Max { get; set; } = 100;
    public long Step { get; set; } = 1;
    public int Scale { get; set; }
    public string? Unit { get; set; }
    public List<string> Range { get; set; } = [];

    [JsonIgnore] public bool CanWrite => Mode != "ro";
    [JsonIgnore] public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Code : Name!;
}

/// <summary>A device known to the app (persisted).</summary>
public sealed class DeviceConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? LocalKey { get; set; }
    public string? Ip { get; set; }
    public string Version { get; set; } = "3.3";
    public string? Category { get; set; }
    public string? ProductId { get; set; }
    public string? ProductName { get; set; }
    public string? Room { get; set; }
    public string? HomeId { get; set; }
    public string? Icon { get; set; }
    /// <summary>True when the device is reachable through the configured Tuya cloud project.</summary>
    public bool InCloud { get; set; }
    /// <summary>When set, this device is controlled through SmartThings (equals the SmartThings deviceId).</summary>
    public string? SmartThingsId { get; set; }
    public bool PreferLocal { get; set; } = true;
    public List<DpSchema> Schema { get; set; } = [];
}

/// <summary>A user defined group of devices controlled together.</summary>
public sealed class GroupConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public List<string> DeviceIds { get; set; } = [];
}

/// <summary>A stored set of DP values (by code) for one device.</summary>
public sealed class SceneAction
{
    public string DeviceId { get; set; } = "";
    public Dictionary<string, JsonElement> Values { get; set; } = [];
}

/// <summary>A tap-to-run scene stored locally.</summary>
public sealed class LocalScene
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public List<SceneAction> Actions { get; set; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScheduleTarget { Device, Group, LocalScene, CloudScene }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScheduleAction { TurnOn, TurnOff, Toggle, RunScene }

/// <summary>A timer executed by the PC app.</summary>
public sealed class ScheduleEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "";
    public TimeOnly Time { get; set; } = new(7, 0);
    public List<DayOfWeek> Days { get; set; } = [.. Enum.GetValues<DayOfWeek>()];
    public ScheduleTarget TargetType { get; set; }
    public string TargetId { get; set; } = "";
    /// <summary>For CloudScene targets: the home the scene belongs to.</summary>
    public string? HomeId { get; set; }
    public ScheduleAction Action { get; set; } = ScheduleAction.TurnOn;
    /// <summary>Optional brightness (1-100 %) applied with TurnOn on lights.</summary>
    public int? Brightness { get; set; }
    public DateTime? LastRun { get; set; }
}

public sealed class CloudSettings
{
    /// <summary>eu, us, cn, in, weu (western Europe), ueaz (eastern America).</summary>
    public string Region { get; set; } = "eu";
    public string? ClientId { get; set; }
    /// <summary>Stored DPAPI-protected (see ConfigStore).</summary>
    public string? ClientSecret { get; set; }
    public string? UserId { get; set; }
}

public sealed class SmartThingsSettings
{
    public string? ClientId { get; set; }
    /// <summary>Stored DPAPI-protected (see ConfigStore).</summary>
    public string? ClientSecret { get; set; }
    /// <summary>Stored DPAPI-protected. Long-lived; access tokens are derived from it at runtime.</summary>
    public string? RefreshToken { get; set; }
    /// <summary>The redirect registered with the SmartThings OAuth app. SmartThings requires HTTPS,
    /// so a code-displaying page such as https://httpbin.org/get is used and the code pasted back.</summary>
    public string RedirectUri { get; set; } = "https://httpbin.org/get";
}

public sealed class AppConfig
{
    /// <summary>UI theme: "Dark" (default) or "Light".</summary>
    public string Theme { get; set; } = "Dark";
    /// <summary>Launch at Windows start-up, minimised to the tray.</summary>
    public bool StartMinimized { get; set; } = true;
    /// <summary>Closing the window hides to the tray instead of exiting.</summary>
    public bool CloseToTray { get; set; } = true;
    public CloudSettings Cloud { get; set; } = new();
    public SmartThingsSettings SmartThings { get; set; } = new();
    public List<DeviceConfig> Devices { get; set; } = [];
    public List<GroupConfig> Groups { get; set; } = [];
    public List<LocalScene> Scenes { get; set; } = [];
    public List<ScheduleEntry> Schedules { get; set; } = [];
}
