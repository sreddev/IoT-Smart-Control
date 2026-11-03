using System.Windows;

namespace LedvanceControl.App.Views;

public partial class HelpWindow : Window
{
    private sealed record Step(string No, string Text);

    public HelpWindow(HelpTopic topic)
    {
        InitializeComponent();
        Title = "Help — " + topic.Title;
        TitleText.Text = topic.Title;

        if (string.IsNullOrWhiteSpace(topic.Intro)) IntroText.Visibility = Visibility.Collapsed;
        else IntroText.Text = topic.Intro;

        StepsList.ItemsSource = topic.Steps.Select((s, i) => new Step((i + 1).ToString(), s)).ToList();

        if (string.IsNullOrWhiteSpace(topic.Tip)) TipBox.Visibility = Visibility.Collapsed;
        else TipText.Text = "Tip:  " + topic.Tip;
    }

    /// <summary>Opens the guide for the given topic id as a modal dialog over the main window.</summary>
    public static void Open(string topicKey)
    {
        var owner = Application.Current?.MainWindow;
        var w = new HelpWindow(HelpTopics.Get(topicKey));
        if (owner != null && owner != w) w.Owner = owner;
        w.ShowDialog();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
