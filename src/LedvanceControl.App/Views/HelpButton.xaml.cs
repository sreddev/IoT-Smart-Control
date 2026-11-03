using System.Windows;
using System.Windows.Controls;

namespace LedvanceControl.App.Views;

/// <summary>A small "?" indicator that opens the step-by-step guide for its <see cref="Topic"/>.</summary>
public partial class HelpButton : UserControl
{
    public static readonly DependencyProperty TopicProperty =
        DependencyProperty.Register(nameof(Topic), typeof(string), typeof(HelpButton), new PropertyMetadata(""));

    public string Topic
    {
        get => (string)GetValue(TopicProperty);
        set => SetValue(TopicProperty, value);
    }

    public HelpButton() => InitializeComponent();

    private void OnClick(object sender, RoutedEventArgs e) => HelpWindow.Open(Topic);
}
