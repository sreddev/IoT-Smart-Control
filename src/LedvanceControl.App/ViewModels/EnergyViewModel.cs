using System.Globalization;
using System.Windows;
using LedvanceControl.App.Mvvm;
using LedvanceControl.Core.Services;

namespace LedvanceControl.App.ViewModels;

/// <summary>
/// Live electrical metering for a plug, reading the Tuya metering data points the Smart+ app uses:
/// cur_power (DP 19), cur_voltage (DP 20), cur_current (DP 18) and add_ele (DP 17, cumulative energy).
/// All values are read-only; they refresh with each device poll.
/// </summary>
public sealed class EnergyViewModel : ObservableObject
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // A standard 230 V / 16 A socket is ~3680 W; used only to size the live power bar.
    private const double BarMaxW = 3680;

    private readonly DeviceRuntime _device;

    public EnergyViewModel(DeviceRuntime device) => _device = device;

    /// <summary>True when the device exposes at least one metering DP (i.e. an energy-monitoring plug).</summary>
    public bool HasMetering => Has("cur_power") || Has("cur_voltage") || Has("cur_current") || Has("add_ele");

    private bool Has(string code) => _device.Config.Schema.Any(s => s.Code == code);

    private double? Scaled(string code)
    {
        // Tuya reports integers (long); SmartThings reports decimals (double).
        double? raw = _device.Get(code) switch { long l => l, double d => d, int i => i, _ => null };
        if (raw is null) return null;
        var scale = _device.Config.Schema.FirstOrDefault(s => s.Code == code)?.Scale ?? 0;
        return raw.Value / Math.Pow(10, scale);
    }

    public double? PowerW => Scaled("cur_power");
    public double? VoltageV => Scaled("cur_voltage");

    public double? CurrentA
    {
        get
        {
            if (Scaled("cur_current") is not double v) return null;
            // Tuya reports current in mA on these plugs; show it in amps.
            var unit = _device.Config.Schema.FirstOrDefault(s => s.Code == "cur_current")?.Unit;
            return string.Equals(unit, "mA", StringComparison.OrdinalIgnoreCase) ? v / 1000.0 : v;
        }
    }

    public double? EnergyKwh => Scaled("add_ele");

    public string PowerText => PowerW is double w ? w.ToString("0.#", Inv) : "—";
    public string VoltageText => VoltageV is double v ? v.ToString("0.#", Inv) : "—";
    public string CurrentText => CurrentA is double a ? a.ToString("0.000", Inv) : "—";
    public string EnergyText => EnergyKwh is double e ? e.ToString("0.##", Inv) : "—";

    public bool HasVoltage => VoltageV != null;
    public bool HasCurrent => CurrentA != null;
    public bool HasEnergy => EnergyKwh != null;

    // Live power bar, expressed as two star-sized grid columns (fill + remainder).
    private double Fraction => PowerW is double w ? Math.Clamp(w / BarMaxW, 0, 1) : 0;
    public GridLength FillStar => new(Fraction, GridUnitType.Star);
    public GridLength RestStar => new(1 - Fraction, GridUnitType.Star);

    public void Load()
    {
        OnPropertyChanged(nameof(HasMetering));
        OnPropertyChanged(nameof(PowerText));
        OnPropertyChanged(nameof(VoltageText));
        OnPropertyChanged(nameof(CurrentText));
        OnPropertyChanged(nameof(EnergyText));
        OnPropertyChanged(nameof(HasVoltage));
        OnPropertyChanged(nameof(HasCurrent));
        OnPropertyChanged(nameof(HasEnergy));
        OnPropertyChanged(nameof(FillStar));
        OnPropertyChanged(nameof(RestStar));
    }
}
