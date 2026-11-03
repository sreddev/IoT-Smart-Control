using System.Windows.Input;
using System.Windows.Media;
using LedvanceControl.App.Mvvm;
using LedvanceControl.Core.Lights;
using LedvanceControl.Core.Services;

namespace LedvanceControl.App.ViewModels;

/// <summary>Something a light panel can drive: a single light or a group of lights.</summary>
public interface ILightTarget
{
    bool HasTemp { get; }
    bool HasColour { get; }
    bool HasScenes { get; }
    Task SetPowerAsync(bool on);
    Task ApplyAsync(Func<LightProfile, IReadOnlyDictionary<string, object?>> build);
}

public sealed record ColourSwatch(string Name, HsvColor Colour)
{
    public Brush Brush { get; } = MakeBrush(Colour);

    private static SolidColorBrush MakeBrush(HsvColor c)
    {
        var (r, g, b) = (c with { V = 1000 }).ToRgb();
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

/// <summary>Power, white (brightness/temperature), colour and scene controls.</summary>
public sealed class LightControlViewModel : ObservableObject
{
    private readonly ILightTarget _target;
    private readonly Debouncer _debounce = new(TimeSpan.FromMilliseconds(250));
    private bool _suppress;
    private bool _isOn;
    private string _mode = "white";
    private double _brightness = 100;
    private double _temperature = 50;
    private double _hue;
    private double _saturation = 100;

    public LightControlViewModel(ILightTarget target)
    {
        _target = target;
        SetModeCommand = new RelayCommand(p => Mode = (string)p!);
        ApplySwatchCommand = new AsyncCommand(p => ApplySwatchAsync((ColourSwatch)p!));
        ApplySceneCommand = new AsyncCommand(p => ApplySceneAsync((string)p!));
    }

    public static IReadOnlyList<ColourSwatch> Swatches { get; } =
    [
        new("Red", new HsvColor(0, 1000, 1000)),
        new("Orange", new HsvColor(30, 1000, 1000)),
        new("Yellow", new HsvColor(55, 1000, 1000)),
        new("Green", new HsvColor(120, 1000, 1000)),
        new("Cyan", new HsvColor(180, 1000, 1000)),
        new("Blue", new HsvColor(230, 1000, 1000)),
        new("Purple", new HsvColor(275, 1000, 1000)),
        new("Pink", new HsvColor(320, 800, 1000)),
    ];

    public static IReadOnlyList<string> SceneNames { get; } = LightProfile.BuiltInScenes.Select(s => s.Name).ToList();

    public bool HasTemp => _target.HasTemp;
    public bool HasColour => _target.HasColour;
    public bool HasScenes => _target.HasScenes;

    /// <summary>Call when the set of capabilities changed (e.g. group membership).</summary>
    public void CapabilitiesChanged()
    {
        OnPropertyChanged(nameof(HasTemp));
        OnPropertyChanged(nameof(HasColour));
        OnPropertyChanged(nameof(HasScenes));
    }

    public ICommand SetModeCommand { get; }
    public ICommand ApplySwatchCommand { get; }
    public ICommand ApplySceneCommand { get; }

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (!Set(ref _isOn, value) || _suppress) return;
            _ = Send(() => _target.SetPowerAsync(value));
        }
    }

    /// <summary>"white", "colour" or "scene".</summary>
    public string Mode
    {
        get => _mode;
        set
        {
            if (!Set(ref _mode, value)) return;
            OnPropertyChanged(nameof(IsWhite));
            OnPropertyChanged(nameof(IsColour));
            OnPropertyChanged(nameof(IsScene));
            if (_suppress) return;
            if (value == "white") SendWhite();
            else if (value == "colour") SendColour();
        }
    }

    public bool IsWhite => Mode == "white";
    public bool IsColour => Mode == "colour";
    public bool IsScene => Mode == "scene";

    /// <summary>1-100 %. In colour mode this is the colour's value (V).</summary>
    public double Brightness
    {
        get => _brightness;
        set
        {
            if (!Set(ref _brightness, Math.Round(value))) return;
            OnPropertyChanged(nameof(PreviewBrush));
            if (_suppress) return;
            if (IsColour) SendColour(); else SendWhite();
        }
    }

    /// <summary>0 = warm, 100 = cold.</summary>
    public double Temperature
    {
        get => _temperature;
        set
        {
            if (!Set(ref _temperature, Math.Round(value))) return;
            OnPropertyChanged(nameof(PreviewBrush));
            if (!_suppress) SendWhite();
        }
    }

    public double Hue
    {
        get => _hue;
        set
        {
            if (!Set(ref _hue, Math.Round(value))) return;
            OnPropertyChanged(nameof(PreviewBrush));
            if (!_suppress) SendColour();
        }
    }

    public double Saturation
    {
        get => _saturation;
        set
        {
            if (!Set(ref _saturation, Math.Round(value))) return;
            OnPropertyChanged(nameof(PreviewBrush));
            if (!_suppress) SendColour();
        }
    }

    public Brush PreviewBrush
    {
        get
        {
            Color c;
            if (IsColour)
            {
                var (r, g, b) = CurrentColour.ToRgb();
                c = Color.FromRgb(r, g, b);
            }
            else
            {
                // blend warm (2700K-ish) to cold (6500K-ish) white, dimmed by brightness
                double t = Temperature / 100, k = 0.25 + Brightness / 100 * 0.75;
                c = Color.FromRgb((byte)(255 * k), (byte)((190 + 60 * t) * k), (byte)((120 + 135 * t) * k));
            }
            var brush = new SolidColorBrush(c);
            brush.Freeze();
            return brush;
        }
    }

    private HsvColor CurrentColour => new((int)Hue, (int)(Saturation * 10), (int)(Math.Max(1, Brightness) * 10));

    /// <summary>Updates the controls from device state without sending anything.</summary>
    public void Load(DeviceRuntime d)
    {
        var p = d.Light;
        if (p == null) return;
        _suppress = true;
        try
        {
            if (d.Get(p.SwitchCode!) is bool on) IsOn = on;
            var mode = p.ModeCode != null ? d.Get(p.ModeCode) as string : null;
            Mode = mode is "colour" or "scene" ? mode : "white";
            if (Mode == "colour" && p.ColourCode != null && HsvColor.TryParse(d.Get(p.ColourCode) as string, out var c))
            {
                Hue = c.H;
                Saturation = c.S / 10.0;
                Brightness = Math.Max(1, c.V / 10.0);
            }
            else if (p.Bright != null && d.Get(p.Bright.Code) is long b)
            {
                Brightness = Math.Max(1, p.BrightnessFromRaw(b));
            }
            if (p.Temp != null && d.Get(p.Temp.Code) is long t) Temperature = p.TempFromRaw(t);
        }
        finally
        {
            _suppress = false;
        }
    }

    private void SendWhite()
    {
        double b = Brightness, t = Temperature;
        SetOnSilently();
        _debounce.Run(() => _target.ApplyAsync(p => p.SetWhite(b, p.HasTemp ? t : null)));
    }

    private void SendColour()
    {
        var c = CurrentColour;
        SetOnSilently();
        _debounce.Run(() => _target.ApplyAsync(p => p.HasColour ? p.SetColour(c) : p.SetWhite(c.V / 10.0, null)));
    }

    private async Task ApplySwatchAsync(ColourSwatch s)
    {
        _suppress = true;
        Mode = "colour";
        Hue = s.Colour.H;
        Saturation = s.Colour.S / 10.0;
        _suppress = false;
        SendColour();
        await Task.CompletedTask;
    }

    private async Task ApplySceneAsync(string name)
    {
        var data = LightProfile.BuiltInScenes.First(s => s.Name == name).Data;
        _suppress = true;
        Mode = "scene";
        _suppress = false;
        SetOnSilently();
        await _target.ApplyAsync(p => p.HasScenes ? p.SetScene(data) : p.SetPower(true));
    }

    private void SetOnSilently()
    {
        _suppress = true;
        IsOn = true;
        _suppress = false;
    }

    private static async Task Send(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { Status.Error(ex); }
    }
}

