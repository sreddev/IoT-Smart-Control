using System.ComponentModel;
using System.Windows;
using LedvanceControl.App.ViewModels;
using Forms = System.Windows.Forms;

namespace LedvanceControl.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private readonly Forms.NotifyIcon _tray;
    private bool _exiting;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        ThemeManager.Apply(_vm.Manager.Config.Theme); // default Dark; applies the saved choice

        // closing the window hides it to the tray so schedules keep running
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => ShowFromTray());
        menu.Items.Add("All lights off", null, (_, _) => _vm.AllOffCommand.Execute(null));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => { _exiting = true; Close(); });
        _tray = new Forms.NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "LEDVANCE Smart+ WiFi Control",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ShowFromTray();

        Loaded += async (_, _) => await _vm.RefreshAllAsync();
    }

    // The tray wants a System.Drawing.Icon; reuse the executable's own (Lumen) icon.
    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            var exe = Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName;
            return System.Drawing.Icon.ExtractAssociatedIcon(exe) ?? System.Drawing.SystemIcons.Application;
        }
        catch
        {
            return System.Drawing.SystemIcons.Application;
        }
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting && _vm.Manager.Config.Schedules.Any(s => s.Enabled))
        {
            e.Cancel = true;
            Hide();
            _tray.ShowBalloonTip(3000, "Still running", "Schedules stay active. Right-click the tray icon to exit.", Forms.ToolTipIcon.Info);
            return;
        }
        base.OnClosing(e);
    }

    protected override async void OnClosed(EventArgs e)
    {
        _tray.Visible = false;
        _tray.Dispose();
        base.OnClosed(e);
        await _vm.DisposeAsync();
        Application.Current.Shutdown();
    }
}
