using System.Collections.ObjectModel;
using System.Windows.Input;
using LedvanceControl.App.Mvvm;
using LedvanceControl.Core.Cloud;
using LedvanceControl.Core.Local;
using LedvanceControl.Core.Model;
using Microsoft.Win32;

namespace LedvanceControl.App.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private string _region;
    private string? _clientId;
    private string? _clientSecret;
    private string _syncProgress = "";
    private DeviceConfig? _selectedDevice;
    private string? _stClientId;
    private string? _stClientSecret;
    private string _stRedirect;
    private string _stManualCode = "";
    private string _stStatus;
    private string _matterCode = "";
    private string _matterIp = "";
    private string _matterStatus;

    public SettingsViewModel(MainViewModel main)
    {
        _main = main;
        var c = main.Manager.Config.Cloud;
        _region = c.Region;
        _clientId = c.ClientId;
        _clientSecret = c.ClientSecret;
        var st = main.Manager.Config.SmartThings;
        _stClientId = st.ClientId;
        _stClientSecret = st.ClientSecret;
        _stRedirect = st.RedirectUri;
        _stStatus = main.Manager.SmartThings?.HasToken == true ? "Connected" : "Not connected";

        SaveCloudCommand = new RelayCommand(SaveCloud);
        SyncCommand = new AsyncCommand(SyncAsync, _ => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret));
        DiscoverCommand = new AsyncCommand(DiscoverAsync);
        ImportCommand = new RelayCommand(Import);
        AddManualCommand = new RelayCommand(AddManual, _ => !string.IsNullOrWhiteSpace(NewDevice.Id));
        SaveDevicesCommand = new RelayCommand(SaveDevices);
        RemoveDeviceCommand = new RelayCommand(RemoveDevice, _ => SelectedDevice != null);

        ConnectStCommand = new AsyncCommand(ConnectStAsync, _ => StReady);
        OpenStSignInCommand = new RelayCommand(OpenStSignIn, _ => StReady);
        ConnectStManualCommand = new AsyncCommand(ConnectStManualAsync, _ => StReady && !string.IsNullOrWhiteSpace(StManualCode));
        SyncStCommand = new AsyncCommand(SyncStAsync, _ => _main.Manager.SmartThings?.HasToken == true);

        _matterStatus = MatterIdleStatus();
        PairMatterCommand = new AsyncCommand(PairMatterAsync, _ => MatterAvailable && !string.IsNullOrWhiteSpace(MatterCode));
        SyncMatterCommand = new AsyncCommand(SyncMatterAsync, _ => MatterAvailable);
        FindMatterCommand = new AsyncCommand(FindMatterAsync, _ => MatterAvailable);
        ReloadDevices();
    }

    private bool StReady => !string.IsNullOrWhiteSpace(StClientId) && !string.IsNullOrWhiteSpace(StClientSecret);

    public IReadOnlyCollection<string> Regions => TuyaCloudClient.Regions;
    public IReadOnlyList<string> Versions { get; } = ["3.1", "3.3", "3.4", "3.5"];
    public IReadOnlyList<string> Categories { get; } = ["dj", "cz", "kg", "pc", "fs", ""];

    public string Region { get => _region; set => Set(ref _region, value); }
    public string? ClientId { get => _clientId; set => Set(ref _clientId, value); }
    public string? ClientSecret { get => _clientSecret; set => Set(ref _clientSecret, value); }
    public string SyncProgress { get => _syncProgress; set => Set(ref _syncProgress, value); }

    public ObservableCollection<DeviceConfig> DeviceConfigs { get; } = [];
    public ObservableCollection<DiscoveredDevice> Discovered { get; } = [];
    public DeviceConfig NewDevice { get; private set; } = new() { Category = "dj" };

    public DeviceConfig? SelectedDevice { get => _selectedDevice; set => Set(ref _selectedDevice, value); }

    public ICommand SaveCloudCommand { get; }
    public ICommand SyncCommand { get; }
    public ICommand DiscoverCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand AddManualCommand { get; }
    public ICommand SaveDevicesCommand { get; }
    public ICommand RemoveDeviceCommand { get; }

    // SmartThings
    public string? StClientId { get => _stClientId; set => Set(ref _stClientId, value); }
    public string? StClientSecret { get => _stClientSecret; set => Set(ref _stClientSecret, value); }
    public string StRedirect { get => _stRedirect; set => Set(ref _stRedirect, value); }
    public string StManualCode { get => _stManualCode; set => Set(ref _stManualCode, value); }
    public string StStatus { get => _stStatus; set => Set(ref _stStatus, value); }
    public ICommand ConnectStCommand { get; }
    public ICommand OpenStSignInCommand { get; }
    public ICommand ConnectStManualCommand { get; }
    public ICommand SyncStCommand { get; }

    // Matter
    public bool MatterAvailable => _main.Manager.Matter != null;
    /// <summary>11/21-digit manual pairing code or the MT:… QR payload.</summary>
    public string MatterCode { get => _matterCode; set => Set(ref _matterCode, value); }
    /// <summary>Optional: the bulb's IP, to skip discovery.</summary>
    public string MatterIp { get => _matterIp; set => Set(ref _matterIp, value); }
    public string MatterStatus { get => _matterStatus; set => Set(ref _matterStatus, value); }
    public ObservableCollection<MatterCommissionableDevice> MatterDiscovered { get; } = [];
    public ICommand PairMatterCommand { get; }
    public ICommand SyncMatterCommand { get; }
    public ICommand FindMatterCommand { get; }

    public string ConfigPath => _main.Manager.ConfigPath;

    /// <summary>Name/room per device as last loaded, to detect edits made in the table.</summary>
    private Dictionary<string, (string Name, string? Room)> _loaded = [];

    public void ReloadDevices()
    {
        DeviceConfigs.Clear();
        foreach (var d in _main.Manager.Config.Devices) DeviceConfigs.Add(d);
        _loaded = _main.Manager.Config.Devices.ToDictionary(d => d.Id, d => (d.Name, d.Room));
    }

    private void SaveCloud()
    {
        var c = _main.Manager.Config.Cloud;
        c.Region = Region;
        c.ClientId = ClientId?.Trim();
        c.ClientSecret = ClientSecret?.Trim();
        _main.Manager.RecreateCloud();
        _main.Manager.Save();
        Status.Info("Cloud settings saved");
    }

    private async Task SyncAsync()
    {
        SaveCloud();
        var progress = new Progress<string>(s => { SyncProgress = s; Status.Info(s); });
        await _main.Manager.SyncFromCloudAsync(progress);
        await _main.RefreshAllAsync();
    }

    private async Task DiscoverAsync()
    {
        Discovered.Clear();
        Status.Info("Listening for device broadcasts (10 s)…");
        var found = await _main.Manager.DiscoverAsync(TimeSpan.FromSeconds(10));
        foreach (var f in found) Discovered.Add(f);
        ReloadDevices();
        Status.Info($"Discovery finished: {found.Count} device(s) seen on the LAN");
        await _main.RefreshAllAsync();
    }

    private void Import()
    {
        var dlg = new OpenFileDialog { Filter = "tinytuya devices.json|*.json|All files|*.*", Title = "Import devices.json" };
        if (dlg.ShowDialog() != true) return;
        var n = _main.Manager.ImportTinyTuya(dlg.FileName);
        ReloadDevices();
        Status.Info($"Imported {n} device(s)");
        _ = _main.RefreshAllAsync();
    }

    private void AddManual()
    {
        var d = NewDevice;
        d.Id = d.Id.Trim();
        if (string.IsNullOrWhiteSpace(d.Name)) d.Name = d.Id;
        if (d.LocalKey is { } k && k.Trim().Length != 16)
        {
            Status.Error("The local key must be exactly 16 characters.");
            return;
        }
        d.LocalKey = d.LocalKey?.Trim();
        d.Schema = d.Category is { Length: > 0 } cat ? DefaultSchemas.Guess(cat, null) : [];
        if (string.IsNullOrEmpty(d.Category)) d.Category = null;
        _main.Manager.AddOrUpdate(d);
        NewDevice = new DeviceConfig { Category = "dj" };
        OnPropertyChanged(nameof(NewDevice));
        ReloadDevices();
        _ = _main.RefreshAllAsync();
    }

    private void SaveDevices()
    {
        // names/rooms edited in the table are pinned so later syncs don't overwrite them
        foreach (var d in DeviceConfigs)
        {
            if (!_loaded.TryGetValue(d.Id, out var before)) continue;
            d.Room = string.IsNullOrWhiteSpace(d.Room) ? null : d.Room.Trim();
            if (d.Name != before.Name) d.NameEdited = true;
            if (!string.Equals(d.Room, before.Room, StringComparison.CurrentCulture)) d.RoomEdited = true;
        }
        // re-create runtimes so connection parameters (ip/key/version) take effect
        foreach (var d in DeviceConfigs.ToList()) _main.Manager.AddOrUpdate(d);
        ReloadDevices();
        Status.Info("Device settings saved");
        _ = _main.RefreshAllAsync();
    }

    private void RemoveDevice()
    {
        if (SelectedDevice == null) return;
        _main.Manager.Remove(SelectedDevice.Id);
        ReloadDevices();
    }

    // ---------------------------------------------------------------- SmartThings

    private void SaveSmartThings()
    {
        var st = _main.Manager.Config.SmartThings;
        st.ClientId = StClientId?.Trim();
        st.ClientSecret = StClientSecret?.Trim();
        st.RedirectUri = string.IsNullOrWhiteSpace(StRedirect) ? "http://localhost:8599/callback" : StRedirect.Trim();
        _main.Manager.RecreateSmartThings();
        _main.Manager.Save();
    }

    private async Task ConnectStAsync()
    {
        SaveSmartThings();
        var client = _main.Manager.SmartThings ?? throw new InvalidOperationException("Enter the SmartThings Client ID and Secret first.");
        var redirect = _main.Manager.Config.SmartThings.RedirectUri;
        if (!redirect.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase) &&
            !redirect.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase))
        {
            // SmartThings only accepts HTTPS redirects: open the page and let the user paste the code.
            OpenStSignIn();
            return;
        }
        StStatus = "Opening browser for SmartThings sign-in…";
        await SmartThingsAuth.ConnectAsync(client, StClientId!.Trim(), redirect);
        StStatus = "Connected";
        Status.Info("Connected to SmartThings. Now press 'Sync SmartThings devices'.");
    }

    private void OpenStSignIn()
    {
        SaveSmartThings();
        var url = SmartThingsClient.BuildAuthorizeUrl(StClientId!.Trim(), _main.Manager.Config.SmartThings.RedirectUri, Guid.NewGuid().ToString("N"));
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        StStatus = "Sign in in the browser, then paste the code below and press 'Use code'.";
        Status.Info("Opened SmartThings sign-in. After approving, copy the 'code' value and paste it below.");
    }

    private async Task ConnectStManualAsync()
    {
        SaveSmartThings();
        var client = _main.Manager.SmartThings ?? throw new InvalidOperationException("Enter the SmartThings Client ID and Secret first.");
        await SmartThingsAuth.ConnectManualAsync(client, StManualCode.Trim(), _main.Manager.Config.SmartThings.RedirectUri);
        StManualCode = "";
        StStatus = "Connected";
        Status.Info("Connected to SmartThings. Now press 'Sync SmartThings devices'.");
    }

    private async Task SyncStAsync()
    {
        var progress = new Progress<string>(s => { SyncProgress = s; Status.Info(s); });
        await _main.Manager.SyncFromSmartThingsAsync(progress);
        ReloadDevices();
        await _main.RefreshAllAsync();
    }

    // ---------------------------------------------------------------- Matter

    private string MatterIdleStatus()
    {
        if (!MatterAvailable) return "Node.js not found. Install Node.js LTS (nodejs.org) and restart the app.";
        var n = _main.Manager.Config.Devices.Count(d => d.MatterNodeId != null);
        return n == 0 ? "Ready · no Matter devices paired yet" : $"Ready · {n} Matter device(s) paired";
    }

    private async Task PairMatterAsync()
    {
        var progress = new Progress<string>(s => { MatterStatus = s; Status.Info(s); });
        try
        {
            var rt = await _main.Manager.PairMatterAsync(MatterCode, string.IsNullOrWhiteSpace(MatterIp) ? null : MatterIp, progress);
            MatterCode = "";
            MatterIp = "";
            ReloadDevices();
            MatterStatus = $"Paired '{rt.Config.Name}'. " + MatterIdleStatus();
        }
        catch
        {
            MatterStatus = "Pairing failed: check the code is fresh (pairing windows close after a few minutes) and the PC is on the same Wi-Fi/LAN.";
            throw;
        }
    }

    private async Task SyncMatterAsync()
    {
        var progress = new Progress<string>(s => { MatterStatus = s; Status.Info(s); });
        await _main.Manager.SyncMatterAsync(progress);
        ReloadDevices();
        await _main.RefreshAllAsync();
    }

    private async Task FindMatterAsync()
    {
        MatterDiscovered.Clear();
        MatterStatus = "Looking for Matter devices in pairing mode (8 s)…";
        var found = await _main.Manager.DiscoverMatterAsync(TimeSpan.FromSeconds(8));
        foreach (var f in found) MatterDiscovered.Add(f);
        MatterStatus = found.Count == 0
            ? "No device has a pairing window open. Open one in the bulb's current app (share / link with another Matter app), then search again."
            : $"{found.Count} device(s) ready to pair. Enter the pairing code and press Pair.";
        // prefer an IPv4 "a.b.c.d:port" address; link-local IPv6 needs a scope id the user can't easily supply
        if (found.Count == 1 && string.IsNullOrWhiteSpace(MatterIp) &&
            found[0].Addresses.FirstOrDefault(a => a.Count(ch => ch == ':') == 1 && a.Contains('.')) is { } v4)
            MatterIp = v4;
    }
}
