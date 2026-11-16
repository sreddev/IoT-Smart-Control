using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using LedvanceControl.App.ViewModels;
using Forms = System.Windows.Forms;

namespace LedvanceControl.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private readonly Forms.NotifyIcon _tray;
    private bool _exiting;
    private bool _trayHintShown;

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
            Text = "IoT Smart Control for Windows",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ShowFromTray();

        // Keep the Windows autostart entry in sync with the saved setting. Whether this launch opens
        // into the tray (started minimized) is decided in App.OnStartup.
        StartupManager.Apply(_vm.Manager.Config.StartMinimized);

        StateChanged += (_, _) => UpdateMaxButton();

        // Navigation items are radio buttons that navigate on click. Screen readers and other UI
        // Automation clients "select" a radio button instead, which checks it without a click, so
        // checking a navigation item by any means navigates too.
        AddHandler(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent, new RoutedEventHandler((_, e) =>
        {
            if (e.OriginalSource is System.Windows.Controls.RadioButton { Command: { } cmd } rb
                && rb.Style == TryFindResource("NavButton") && cmd.CanExecute(rb.CommandParameter))
                cmd.Execute(rb.CommandParameter);
        }));

        // Auto refresh: once at launch, even when starting minimized to the tray (the window isn't shown
        // then, so this can't wait for Loaded), and again whenever the window comes back from the tray or
        // from minimized, if the last refresh started more than 30 s ago.
        Dispatcher.BeginInvoke(() => _ = _vm.RefreshAllAsync(), System.Windows.Threading.DispatcherPriority.Background);
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) _vm.RefreshIfStale(RefreshAfter); };
        StateChanged += (_, _) =>
        {
            if (_lastState == WindowState.Minimized && WindowState != WindowState.Minimized) _vm.RefreshIfStale(RefreshAfter);
            _lastState = WindowState;
        };
    }

    private static readonly TimeSpan RefreshAfter = TimeSpan.FromSeconds(30);
    private WindowState _lastState = WindowState.Normal;

    // ---- custom title bar / window chrome ----

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource src) src.AddHook(WndProc);
        UpdateMaxButton();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaxRestore_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseWin_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateMaxButton()
    {
        if (MaxBtn == null) return;
        MaxBtn.Content = WindowState == WindowState.Maximized ? "" : ""; // restore / maximize
        MaxBtn.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximise";
    }

    // Keep a borderless (WindowChrome) window from covering the taskbar when maximized.
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_GETMINMAXINFO = 0x0024;
        const int MONITOR_DEFAULTTONEAREST = 0x2;
        if (msg == WM_GETMINMAXINFO)
        {
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero)
            {
                var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                GetMonitorInfo(monitor, ref info);
                var work = info.rcWork;
                var mon = info.rcMonitor;
                mmi.ptMaxPosition.X = work.Left - mon.Left;
                mmi.ptMaxPosition.Y = work.Top - mon.Top;
                mmi.ptMaxSize.X = work.Right - work.Left;
                mmi.ptMaxSize.Y = work.Bottom - work.Top;
                Marshal.StructureToPtr(mmi, lParam, true);
            }
        }
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO { public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public int dwFlags; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

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
        if (!_exiting && _vm.Manager.Config.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            if (!_trayHintShown)
            {
                _tray.ShowBalloonTip(3000, "Still running", "IoT Smart Control is in the tray — schedules stay active. Right-click the icon to exit.", Forms.ToolTipIcon.Info);
                _trayHintShown = true;
            }
            return;
        }
        base.OnClosing(e);
    }

    /// <summary>Runs between "everything stopped" and the restart (see <see cref="RestartAfter"/>).</summary>
    private Action? _whileStopped;

    /// <summary>
    /// Stops the app completely (devices, Matter bridge, scheduler), runs <paramref name="whileStopped"/>
    /// (e.g. writing an imported configuration, so nothing running can overwrite it) and starts the app
    /// again with the window shown.
    /// </summary>
    public void RestartAfter(Action whileStopped)
    {
        _whileStopped = whileStopped;
        _exiting = true;
        Close();
    }

    protected override async void OnClosed(EventArgs e)
    {
        _tray.Visible = false;
        _tray.Dispose();
        base.OnClosed(e);
        await _vm.DisposeAsync();
        if (_whileStopped != null)
        {
            try
            {
                _whileStopped();
                if (Environment.ProcessPath is { } exe) System.Diagnostics.Process.Start(exe);
            }
            catch (Exception ex)
            {
                MessageBox.Show("The settings could not be applied: " + ex.Message, "IoT Smart Control", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        Application.Current.Shutdown();
    }
}
