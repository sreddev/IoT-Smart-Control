using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LedvanceControl.App;

/// <summary>Minimal helpers for showing text results (diagnostics) in a scrollable window.</summary>
public static class DialogService
{
    public static void ShowText(string title, string text)
    {
        var box = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            BorderThickness = new Thickness(0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(14),
        };
        var window = new Window
        {
            Title = title,
            Width = 640,
            Height = 480,
            Content = box,
            Owner = Application.Current?.MainWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        window.ShowDialog();
    }
}
