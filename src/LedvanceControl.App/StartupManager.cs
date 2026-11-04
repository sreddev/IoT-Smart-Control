using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace LedvanceControl.App;

/// <summary>
/// Registers the app to launch at Windows sign-in (minimised to the tray) via the per-user Run key.
/// Skipped when running as a packaged (MSIX) app, where start-up is driven by the manifest StartupTask.
/// </summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "IoTSmartControl";

    public const string MinimizedArg = "--minimized";

    private static string ExePath =>
        Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName;

    /// <summary>True for an MSIX-packaged run (exe lives under WindowsApps); Run-key autostart is not used there.</summary>
    public static bool IsPackaged =>
        ExePath.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);

    /// <summary>Adds or removes the Run-key entry so it matches <paramref name="enabled"/>.</summary>
    public static void Apply(bool enabled)
    {
        if (IsPackaged) return;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (key == null) return;
            if (enabled) key.SetValue(ValueName, $"\"{ExePath}\" {MinimizedArg}");
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch
        {
            // Registry access can fail under locked-down policies; autostart is best-effort.
        }
    }

    /// <summary>True when this process was started by the autostart entry (so it should open in the tray).</summary>
    public static bool LaunchedMinimized =>
        Environment.GetCommandLineArgs().Any(a => string.Equals(a, MinimizedArg, StringComparison.OrdinalIgnoreCase));
}
