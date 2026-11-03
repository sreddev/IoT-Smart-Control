using System.Windows;

namespace LedvanceControl.App;

/// <summary>Swaps the active colour palette (index 0 of the app's merged dictionaries) at runtime.</summary>
public static class ThemeManager
{
    public const string Dark = "Dark";
    public const string Light = "Light";

    public static string Current { get; private set; } = Dark;

    public static void Apply(string theme)
    {
        theme = theme == Light ? Light : Dark;
        var app = Application.Current;
        if (app == null) return;
        var dict = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/LedvanceControl;component/Themes/{theme}.xaml"),
        };
        var merged = app.Resources.MergedDictionaries;
        if (merged.Count == 0) merged.Add(dict);
        else merged[0] = dict; // palette is always the first entry (see App.xaml)
        Current = theme;
    }

    public static void Toggle() => Apply(Current == Dark ? Light : Dark);
}
