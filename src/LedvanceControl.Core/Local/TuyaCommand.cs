namespace LedvanceControl.Core.Local;

/// <summary>Tuya LAN protocol command codes.</summary>
public enum TuyaCommand : uint
{
    ApConfig = 0x01,
    Active = 0x02,
    SessKeyNegStart = 0x03,
    SessKeyNegResp = 0x04,
    SessKeyNegFinish = 0x05,
    Unbind = 0x06,
    Control = 0x07,
    Status = 0x08,
    HeartBeat = 0x09,
    DpQuery = 0x0A,
    QueryWifi = 0x0B,
    TokenBind = 0x0C,
    ControlNew = 0x0D,
    EnableWifi = 0x0E,
    DpQueryNew = 0x10,
    SceneExecute = 0x11,
    UpdateDps = 0x12,
    UdpNew = 0x13,
    ApConfigNew = 0x14,
    BroadcastLpv34 = 0x23,
    LanExtStream = 0x40,
}

/// <summary>Supported LAN protocol versions.</summary>
public enum TuyaVersion
{
    V31,
    V33,
    V34,
    V35,
}

public static class TuyaVersionExtensions
{
    public static TuyaVersion Parse(string? s) => s?.Trim() switch
    {
        "3.1" => TuyaVersion.V31,
        "3.4" => TuyaVersion.V34,
        "3.5" => TuyaVersion.V35,
        _ => TuyaVersion.V33,
    };

    public static string ToText(this TuyaVersion v) => v switch
    {
        TuyaVersion.V31 => "3.1",
        TuyaVersion.V34 => "3.4",
        TuyaVersion.V35 => "3.5",
        _ => "3.3",
    };
}
