namespace LedvanceControl.App;

/// <summary>One step-by-step help topic shown by the in-app guide.</summary>
public sealed class HelpTopic
{
    public required string Title { get; init; }
    public string? Intro { get; init; }
    public required IReadOnlyList<string> Steps { get; init; }
    public string? Tip { get; init; }
}

/// <summary>Central registry of in-app help, keyed by a short topic id (also used by <c>HelpButton.Topic</c>).</summary>
public static class HelpTopics
{
    public static HelpTopic Get(string key) =>
        All.TryGetValue(key, out var t) ? t : new HelpTopic { Title = key, Steps = ["No help available for this item yet."] };

    public static readonly IReadOnlyDictionary<string, HelpTopic> All = new Dictionary<string, HelpTopic>
    {
        ["Devices"] = new()
        {
            Title = "Devices",
            Intro = "Control your lights and plugs. Devices are grouped by room on the left; selecting one opens its panel on the right.",
            Steps =
            [
                "Pick a device in the left list. Under its name a status line shows how it is reached: Online · LAN (direct, local), Online · Cloud (via Tuya), Online · SmartThings, or Offline with the reason.",
                "Flip the switch on a card — or in the panel header — to turn a device on or off. 'All off' at the top turns everything off at once.",
                "For lights, use the panel tabs: White (brightness + colour temperature), Colour (hue / saturation / brightness plus quick colours) and Scenes (the 8 built-in Tuya light effects).",
                "For energy-monitoring plugs, an Energy monitor panel shows live power draw (W), voltage (V), current (A) and total energy (kWh). It appears only when the device actually reports metering.",
                "Refresh re-reads the current state. Diagnostics runs connectivity checks and reports exactly how the device is reached.",
                "The Data points card lists every raw function the device exposes; read-only values such as voltage are shown on the right.",
            ],
            Tip = "Control prefers your local network (LAN) whenever an IP address and 16-character local key are known, and falls back to the cloud otherwise.",
        },

        ["Groups"] = new()
        {
            Title = "Groups",
            Intro = "Control several devices together, like the Groups tab of the mobile app.",
            Steps =
            [
                "Click '+ New group' and give the group a name.",
                "Select the group, then tick the devices that should belong to it.",
                "Use the group's controls to switch power, set brightness/colour, or run a scene on every member at once.",
                "Edit or delete a group at any time. Removing a device from the app also removes it from every group.",
            ],
        },

        ["Scenes"] = new()
        {
            Title = "Scenes",
            Intro = "Save and replay device states, and use scenes/automations you created in the Tuya app.",
            Steps =
            [
                "Local scene: set your devices exactly how you want them, then capture the current state as a named scene (stored on this PC).",
                "Press Run on a local scene to re-apply those states to all of its devices.",
                "Cloud scenes (from Tuya) can be run with one click; cloud automations can be enabled or disabled.",
                "Cloud scenes and automations appear after you synchronise from the Tuya cloud project in Settings.",
            ],
        },

        ["Schedules"] = new()
        {
            Title = "Schedules",
            Intro = "Run actions automatically at a time of day. Timers are run by this app while it is open.",
            Steps =
            [
                "Add a schedule and choose a target: a device, a group or a scene.",
                "Set the time of day and tick the days of the week it should run.",
                "Pick the action (on / off / toggle, or run scene). For lights you can also set a brightness.",
                "Enable the schedule with its switch.",
                "While any schedule is enabled, closing the window keeps the app running in the system tray so timers still fire. Right-click the tray icon to reopen or exit.",
            ],
            Tip = "Schedules only run while the app (or its tray icon) is running — they are not Windows scheduled tasks.",
        },

        ["Settings"] = new()
        {
            Title = "Settings",
            Intro = "Connect your devices and configure the app. Each section below has its own ? with a detailed, step-by-step guide.",
            Steps =
            [
                "App settings — theme, start-minimized (autostart to tray) and close-to-tray.",
                "Tuya cloud project — link your Tuya account to import devices, local keys and schemas (this enables local LAN control).",
                "SmartThings — connect bulbs that are brand-locked to LEDVANCE but visible in Samsung SmartThings.",
                "Local network — discover device IP addresses for LAN control, or import a tinytuya devices.json.",
                "Devices — edit names, rooms and connection details for each known device.",
                "Add a device manually — enter a device's id, key, IP and type by hand.",
            ],
            Tip = "You usually need only one connection route per device: Tuya for full local control, or SmartThings for brand-locked bulbs.",
        },

        ["Settings.Appearance"] = new()
        {
            Title = "App settings",
            Intro = "Appearance and start-up/close behaviour for the app.",
            Steps =
            [
                "Theme — toggle between Light and Dark. Dark is the default; the choice applies immediately and is remembered.",
                "Start minimized — when on, the app launches automatically at Windows sign-in and opens straight into the system tray (default: on).",
                "Close to tray — when on, closing the window keeps the app running in the tray (so schedules keep firing) instead of exiting; use the tray icon's Exit to quit (default: on).",
            ],
        },

        ["Settings.Tuya"] = new()
        {
            Title = "Tuya cloud project",
            Intro = "Link your Tuya IoT project to import devices and their local keys. This is what enables fast, cloud-free LAN control.",
            Steps =
            [
                "Create a free account at iot.tuya.com in the data centre for your region (Eastern Europe → Central Europe / 'eu').",
                "In the Tuya IoT console open Cloud → Development → Create Cloud Project. After it is created, note the Access ID (Client ID) and Access Secret (Client Secret).",
                "On the project's Service API tab, subscribe (free) to 'IoT Core' and 'Smart Home Scene Linkage'.",
                "Open Devices → Link App Account → Add App Account and scan the QR code with your LEDVANCE / Smart Life app (Me → scan icon).",
                "If your LEDVANCE account can't be linked (only Tuya-platform apps can be), add the devices to the free Smart Life app and link that account instead.",
                "Back in this app: choose the Data centre, paste the Access ID and Access Secret, then click 'Save & synchronise devices'. Devices, local keys, DP schemas and rooms import.",
                "Finally open the Local network section and run 'Discover devices' so the app learns each device's IP. From then on control runs over the LAN.",
            ],
            Tip = "The Access Secret is stored encrypted with Windows DPAPI. Re-sync any time to pull new devices; devices removed from the project are pruned automatically.",
        },

        ["Settings.SmartThings"] = new()
        {
            Title = "SmartThings",
            Intro = "Control bulbs that are brand-locked to LEDVANCE (and so can't be re-paired to Tuya) but are visible in Samsung SmartThings.",
            Steps =
            [
                "Make sure the device already works in the SmartThings app on your phone.",
                "Install the SmartThings CLI and run: smartthings apps:create — choose 'OAuth-In App'.",
                "Set the scopes to: r:devices:*  x:devices:*  w:devices:*  r:locations:*",
                "For the redirect URI, SmartThings rejects http://localhost, so use an HTTPS page that simply echoes the returned code, e.g. https://httpbin.org/get. Note the generated Client ID and Client Secret.",
                "In this app enter the Client ID, Client Secret and the same Redirect URI you registered.",
                "Click 'Open sign-in page', approve access in the browser, and copy the 'code' value from the resulting URL / page.",
                "Paste the code, click 'Use code', then 'Sync SmartThings devices'. The status shows Connected.",
            ],
            Tip = "SmartThings control needs internet. Live metering (power / voltage / current) only appears if SmartThings exposes it for the device — many plugs bridged from Tuya expose on/off only.",
        },

        ["Settings.Lan"] = new()
        {
            Title = "Local network",
            Intro = "Find devices on your Wi-Fi for direct, cloud-free control.",
            Steps =
            [
                "Click 'Discover devices (10 s)'. The app listens for the UDP broadcasts devices send (ports 6666/6667/7000).",
                "Allow the app through the Windows firewall if prompted — otherwise discovery hears nothing.",
                "Discovered devices automatically fill in their IP address and protocol version, matched to known devices by id.",
                "Already use tinytuya? Click 'Import tinytuya devices.json…' to load devices (id, key, IP) from a wizard export.",
            ],
            Tip = "LAN control needs each device's 16-character local key, obtained from a Tuya cloud sync or the tinytuya import.",
        },

        ["Settings.Devices"] = new()
        {
            Title = "Devices (connection details)",
            Intro = "Edit how each known device is identified and connected.",
            Steps =
            [
                "Edit Name and Room to organise your devices.",
                "Set the IP address, protocol Version and Local key used for LAN control.",
                "'LAN first' uses the local network when an IP and key are known; otherwise the cloud is used.",
                "'Cloud' (read-only) indicates the device came from a cloud sync.",
                "Click 'Apply changes' to save edits, or 'Remove selected' to delete a device.",
            ],
        },

        ["Settings.Manual"] = new()
        {
            Title = "Add a device manually",
            Intro = "Add a device by hand when you already know its details.",
            Steps =
            [
                "Enter a Name and the Device id.",
                "Enter the 16-character Local key and the device's IP address.",
                "Choose the protocol Version (3.1 / 3.3 / 3.4 / 3.5) and the Type: dj = light, cz = plug, kg = switch.",
                "Click 'Add device'. A matching data-point schema is applied automatically — for example, plugs get the metering data points.",
            ],
            Tip = "Use this for devices that discovery can't find, or when importing keys from another tool.",
        },
    };
}
