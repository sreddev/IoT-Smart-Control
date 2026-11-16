using System.IO;
using System.Windows;
using LedvanceControl.App.Mvvm;
using LedvanceControl.Core.Backup;
using LedvanceControl.Core.Services;
using Microsoft.Win32;

namespace LedvanceControl.App.Views;

/// <summary>
/// Export dialog: the user picks the parts to include and, when keys are included, a password; the file
/// is then written with <see cref="SettingsBackup.Export"/>. Passwords live only in the PasswordBoxes and
/// are never stored.
/// </summary>
public partial class ExportBackupWindow : Window
{
    private readonly DeviceManager _manager;

    public ExportBackupWindow(DeviceManager manager)
    {
        _manager = manager;
        InitializeComponent();
        var c = manager.Config;
        DevicesCount.Text = $"{Plural(c.Houses.Count, "house")} · {Plural(c.Houses.Sum(h => h.Rooms.Count), "room")} · {Plural(c.Devices.Count, "device")}";
        AutomationsCount.Text = $"{Plural(c.Groups.Count, "group")} · {Plural(c.Scenes.Count, "scene")} · {Plural(c.Schedules.Count, "schedule")}";
        UpdatePasswordPanel();
    }

    /// <summary>The parts ticked in the dialog.</summary>
    public BackupParts SelectedParts =>
        (DevicesBox.IsChecked == true ? BackupParts.Devices : 0)
        | (AutomationsBox.IsChecked == true ? BackupParts.Automations : 0)
        | (PreferencesBox.IsChecked == true ? BackupParts.Preferences : 0)
        | (SecretsBox.IsChecked == true ? BackupParts.Secrets : 0);

    private static string Plural(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";

    private void SecretsBox_Changed(object sender, RoutedEventArgs e) => UpdatePasswordPanel();

    private void Password_Changed(object sender, RoutedEventArgs e) => ShowError(null);

    private void UpdatePasswordPanel()
    {
        if (PasswordPanel == null) return; // during InitializeComponent
        PasswordPanel.Visibility = SecretsBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ShowError(null);
    }

    private void ShowError(string? text)
    {
        if (ErrorText == null) return;
        ErrorText.Text = text ?? "";
        ErrorText.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Checks the input; returns the password to use (null = unencrypted) or an error.</summary>
    public (string? Password, string? Error) Validate()
    {
        var parts = SelectedParts;
        if ((parts & ~BackupParts.Secrets) == BackupParts.None) return (null, "Choose at least one thing to export.");
        if (!parts.HasFlag(BackupParts.Secrets)) return (null, null);
        if (Password1.Password.Length < SettingsBackup.MinPasswordLength)
            return (null, $"Use a password with at least {SettingsBackup.MinPasswordLength} characters.");
        if (Password1.Password != Password2.Password) return (null, "The two passwords don't match.");
        return (Password1.Password, null);
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var (password, error) = Validate();
        if (error != null) { ShowError(error); return; }

        var dlg = new SaveFileDialog
        {
            Title = "Export settings",
            FileName = SettingsBackup.SuggestedFileName(),
            DefaultExt = SettingsBackup.FileExtension,
            Filter = $"IoT Smart Control backup (*{SettingsBackup.FileExtension})|*{SettingsBackup.FileExtension}",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var version = typeof(ExportBackupWindow).Assembly.GetName().Version?.ToString(3);
            var bytes = SettingsBackup.Export(_manager.Config, SelectedParts, password, version);
            File.WriteAllBytes(dlg.FileName, bytes);
            Status.Info($"Settings exported to {Path.GetFileName(dlg.FileName)}" + (password != null ? " (keys encrypted)." : " (without keys)."));
            DialogResult = true;
        }
        catch (Exception ex) when (ex is BackupException or IOException or UnauthorizedAccessException)
        {
            ShowError(ex.Message);
        }
    }
}
