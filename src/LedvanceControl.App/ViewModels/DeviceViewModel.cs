using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using LedvanceControl.App.Mvvm;
using LedvanceControl.Core.Lights;
using LedvanceControl.Core.Model;
using LedvanceControl.Core.Services;

namespace LedvanceControl.App.ViewModels;

public sealed class DeviceViewModel : ObservableObject, ILightTarget
{
    private bool _isOn;
    private bool _suppress;
    private bool _isSelected;

    public DeviceViewModel(DeviceRuntime runtime)
    {
        Runtime = runtime;
        Light = new LightControlViewModel(this);
        Energy = new EnergyViewModel(runtime);
        RefreshCommand = new AsyncCommand(() => Runtime.RefreshAsync());
        DiagnosticsCommand = new AsyncCommand(async () =>
        {
            Status.Info($"Running diagnostics on {Name}…");
            var report = await Runtime.DiagnosticsAsync();
            DialogService.ShowText($"Diagnostics — {Name}", report);
            Status.Info($"Diagnostics complete for {Name}");
        });
        RebuildDps();
        Runtime.Changed += _ => Application.Current?.Dispatcher.BeginInvoke(Update);
        Update();
    }

    public DeviceRuntime Runtime { get; }
    public string Id => Runtime.Id;
    public string Name => Runtime.Config.Name;
    public string Room => string.IsNullOrWhiteSpace(Runtime.Config.Room) ? "No room" : Runtime.Config.Room!;
    public string Subtitle => Runtime.Config.ProductName ?? Runtime.Config.Category ?? Runtime.Config.Id;
    public bool IsLight => Runtime.Light != null;
    public bool HasPower => Runtime.PowerCode != null;
    public LightControlViewModel Light { get; }
    public EnergyViewModel Energy { get; }
    public ObservableCollection<DpViewModel> Dps { get; } = [];
    public ICommand RefreshCommand { get; }
    public ICommand DiagnosticsCommand { get; }

    /// <summary>Used by checklists (groups, scene capture).</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public string Glyph => Runtime.Config.Category switch
    {
        "cz" or "pc" => "",           // plug
        "kg" or "tdq" => "",          // switch
        "fs" => "",                   // fan
        _ when IsLight => "",         // light bulb
        _ => "",                      // generic
    };

    public string StatusText => Runtime.Online
        ? Runtime.LastTransport switch { Transport.Lan => "Online · LAN", Transport.Cloud => "Online · Cloud", Transport.SmartThings => "Online · SmartThings", _ => "Online" }
        : Runtime.LastError != null ? "Offline · " + Runtime.LastError : "Not refreshed";

    public bool IsOnline => Runtime.Online;

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (!Set(ref _isOn, value) || _suppress) return;
            _ = SendPower(value);
        }
    }

    private async Task SendPower(bool on)
    {
        try { await Runtime.SetPowerAsync(on); }
        catch (Exception ex) { Status.Error($"{Name}: {ex.Message}"); }
    }

    public void RebuildDps()
    {
        Dps.Clear();
        foreach (var s in Runtime.Config.Schema) Dps.Add(new DpViewModel(Runtime, s));
        OnPropertyChanged(nameof(IsLight));
        OnPropertyChanged(nameof(HasPower));
        OnPropertyChanged(nameof(Glyph));
    }

    private void Update()
    {
        if (Dps.Count != Runtime.Config.Schema.Count) RebuildDps();
        _suppress = true;
        IsOn = Runtime.IsOn == true;
        _suppress = false;
        Light.Load(Runtime);
        Energy.Load();
        foreach (var dp in Dps) dp.Load();
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsOnline));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Room));
    }

    // ILightTarget
    bool ILightTarget.HasTemp => Runtime.Light?.HasTemp == true;
    bool ILightTarget.HasColour => Runtime.Light?.HasColour == true;
    bool ILightTarget.HasScenes => Runtime.Light?.HasScenes == true;
    Task ILightTarget.SetPowerAsync(bool on) => Runtime.SetPowerAsync(on);
    Task ILightTarget.ApplyAsync(Func<LightProfile, IReadOnlyDictionary<string, object?>> build) =>
        Runtime.Light == null ? Task.CompletedTask : Runtime.SetAsync(build(Runtime.Light));
}

/// <summary>Generic editor for one data point, driven by its schema.</summary>
public sealed class DpViewModel : ObservableObject
{
    private readonly DeviceRuntime _device;
    private readonly Debouncer _debounce = new(TimeSpan.FromMilliseconds(300));
    private bool _suppress;
    private bool _bool;
    private long _raw;
    private string? _enum;
    private string _text = "";

    public DpViewModel(DeviceRuntime device, DpSchema schema)
    {
        _device = device;
        Schema = schema;
        SendTextCommand = new AsyncCommand(SendTextAsync);
    }

    public DpSchema Schema { get; }
    public string Label => $"{Schema.DisplayName}  (DP {Schema.Id})";
    public bool ReadOnly => !Schema.CanWrite;
    public bool IsBool => Schema.Type == DpType.Bool && !ReadOnly;
    public bool IsNumber => Schema.Type == DpType.Value && !ReadOnly;
    public bool IsEnum => Schema.Type == DpType.Enum && !ReadOnly;
    public bool IsText => Schema.Type is DpType.String or DpType.Json or DpType.Raw or DpType.Bitmap && !ReadOnly;
    public IReadOnlyList<string> Options => Schema.Range;
    public ICommand SendTextCommand { get; }

    public string Display
    {
        get
        {
            var v = _device.Get(Schema.Code);
            return v switch
            {
                null => "–",
                long l when Schema.Type == DpType.Value => (l / Math.Pow(10, Schema.Scale)).ToString(CultureInfo.CurrentCulture) + (Schema.Unit is { Length: > 0 } u ? " " + u : ""),
                double d when Schema.Type == DpType.Value => (d / Math.Pow(10, Schema.Scale)).ToString("0.###", CultureInfo.CurrentCulture) + (Schema.Unit is { Length: > 0 } u ? " " + u : ""),
                bool b => b ? "On" : "Off",
                _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
            };
        }
    }

    public bool BoolValue
    {
        get => _bool;
        set { if (Set(ref _bool, value) && !_suppress) Send(value); }
    }

    /// <summary>Slider position 0-1000 mapped onto the DP range (the slider range is fixed to avoid WPF coercion issues).</summary>
    public double SliderPosition
    {
        get => Schema.Max == Schema.Min ? 0 : (_raw - Schema.Min) * 1000.0 / (Schema.Max - Schema.Min);
        set
        {
            var step = Math.Max(1, Schema.Step);
            var raw = Schema.Min + (long)Math.Round((Schema.Max - Schema.Min) * value / 1000.0 / step) * step;
            raw = Math.Clamp(raw, Schema.Min, Schema.Max);
            if (raw == _raw) return;
            _raw = raw;
            OnPropertyChanged();
            if (_suppress) return;
            OnPropertyChanged(nameof(Display));
            _debounce.Run(() => _device.SetAsync(Schema.Code, raw));
        }
    }

    public string? EnumValue
    {
        get => _enum;
        set { if (Set(ref _enum, value) && !_suppress && value != null) Send(value); }
    }

    public string Text
    {
        get => _text;
        set => Set(ref _text, value);
    }

    public void Load()
    {
        _suppress = true;
        var v = _device.Get(Schema.Code);
        switch (v)
        {
            case bool b: BoolValue = b; break;
            case long l:
                _raw = l;
                OnPropertyChanged(nameof(SliderPosition));
                break;
            case string s when Schema.Type == DpType.Enum: EnumValue = s; break;
        }
        if (v != null && Schema.Type != DpType.Bool && Schema.Type != DpType.Value)
            Text = v is string str ? str : JsonSerializer.Serialize(v);
        _suppress = false;
        OnPropertyChanged(nameof(Display));
    }

    private async void Send(object value)
    {
        try { await _device.SetAsync(Schema.Code, value); }
        catch (Exception ex) { Status.Error($"{_device.Config.Name}: {ex.Message}"); }
    }

    private Task SendTextAsync() => _device.SetAsync(Schema.Code, Text);
}


