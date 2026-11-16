using System.IO;
using System.Windows;
using LedvanceControl.Core.Backup;
using LedvanceControl.Core.Model;
using LedvanceControl.Core.Services;

namespace LedvanceControl.App.Views;

/// <summary>
/// Import preview. Shows the backup's header (no password needed), unlocks the content with the
/// password, combines it with the current settings (<see cref="SettingsBackup.Merge"/>) and hands the
/// result to <see cref="MainWindow.RestartAfter"/>: the app stops, a safety copy of the current file is
/// saved, the new configuration is written, and the app starts again.
/// </summary>
public partial class ImportBackupWindow : Window
{
    private readonly DeviceManager _manager;
    private readonly BackupFile _file;

    public ImportBackupWindow(DeviceManager manager, BackupFile file, string path)
    {
        _manager = manager;
        _file = file;
        InitializeComponent();
        var h = file.Header;
        FileText.Text = Path.GetFileName(path);
        CreatedText.Text = $"{h.Created.LocalDateTime:g}" + (string.IsNullOrEmpty(h.Computer) ? "" : $" on {h.Computer}") + (h.AppVersion != null ? $" (v{h.AppVersion})" : "");
        ContainsText.Text = Describe(h);
        KeysText.Text = h.Parts.HasFlag(BackupParts.Secrets) ? "Included, password protected"
            : h.Encrypted ? "Not included (file is password protected)" : "Not included";
        PasswordPanel.Visibility = h.Encrypted ? Visibility.Visible : Visibility.Collapsed;
        if (h.Encrypted) Loaded += (_, _) => Password.Focus();
    }

    /// <summary>"3 houses, 6 devices … · preferences".</summary>
    public static string Describe(BackupHeader h)
    {
        var items = new List<string>();
        if (h.Parts.HasFlag(BackupParts.Devices) || h.Parts.HasFlag(BackupParts.Automations)) items.Add(h.Summary.ToString());
        if (h.Parts.HasFlag(BackupParts.Preferences)) items.Add("app preferences");
        return items.Count == 0 ? "Nothing to import" : string.Join(" · ", items);
    }

    public ImportMode Mode => ReplaceChoice.IsChecked == true ? ImportMode.Replace : ImportMode.Merge;

    private void Password_Changed(object sender, RoutedEventArgs e) => ShowError(null);

    private void ShowError(string? text)
    {
        ErrorText.Text = text ?? "";
        ErrorText.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Unlocks and merges; returns the configuration to save, or shows why it can't.</summary>
    public AppConfig? Prepare()
    {
        try
        {
            var incoming = SettingsBackup.Open(_file, _file.Header.Encrypted ? Password.Password : null);
            return SettingsBackup.Merge(_manager.Config, incoming, _file.Header.Parts, Mode);
        }
        catch (BackupException ex)
        {
            ShowError(ex.Message);
            Password.SelectAll();
            Password.Focus();
            return null;
        }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var merged = Prepare();
        if (merged == null) return;
        var store = _manager.Store;
        DialogResult = true;
        // Everything is stopped before the file is touched, so no running part can overwrite it.
        ((MainWindow)Application.Current.MainWindow).RestartAfter(() =>
        {
            SettingsBackup.SaveSafetyCopy(store);
            store.Save(merged);
        });
    }
}
