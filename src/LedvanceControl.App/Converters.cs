using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LedvanceControl.App;

/// <summary>true → Visible (or Collapsed when ConverterParameter is "invert").</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool b = value is true;
        if (parameter as string == "invert") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Visible when the bound value equals the ConverterParameter (string compare).</summary>
public sealed class EqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>True when the bound value equals the ConverterParameter (for nav highlighting).</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>List of days → "Mon Tue …" / "Every day".</summary>
public sealed class DaysConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not IEnumerable<DayOfWeek> days) return "";
        var list = days.Distinct().ToList();
        if (list.Count == 7) return "Every day";
        if (list.Count == 0) return "Never";
        return string.Join(" ", list.OrderBy(d => ((int)d + 6) % 7).Select(d => culture.DateTimeFormat.AbbreviatedDayNames[(int)d]));
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>TimeOnly → "HH:mm".</summary>
public sealed class TimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture) =>
        value is TimeOnly t ? t.ToString("HH:mm", CultureInfo.InvariantCulture) : "";

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>null → Collapsed, otherwise Visible.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value == null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
