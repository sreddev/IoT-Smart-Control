using System.Windows;

namespace LedvanceControl.App;

/// <summary>Interaction logic for App.xaml.</summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The window is always created (its tray icon + view-model come up with it), but it is only
        // shown when the app was NOT launched by the autostart entry. Started minimized, it lives in
        // the tray until the user opens it. ShutdownMode is OnExplicitShutdown (see App.xaml) so the
        // process keeps running with no visible window.
        var window = new MainWindow();
        MainWindow = window;
        if (!StartupManager.LaunchedMinimized) window.Show();
    }
}
