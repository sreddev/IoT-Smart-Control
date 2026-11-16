using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace LedvanceControl.Core.Security;

/// <summary>
/// Keeps the app's data readable by its owner only.
/// Linux/macOS: directories 0700, files 0600. Windows: the data directory gets an explicit ACL with no
/// inherited entries — the current user and SYSTEM only — which its files and subfolders inherit.
/// </summary>
public static class FilePermissions
{
    private const UnixFileMode DirMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>Unix create mode for new files (pass to <see cref="FileStreamOptions.UnixCreateMode"/>).</summary>
    public static UnixFileMode PrivateFileMode => FileMode;

    /// <summary>Creates the directory if needed and restricts it (recursively for what is already inside).</summary>
    public static void SecureDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            SecureDirectoryWindows(path);
            return;
        }
        if (!Directory.Exists(path)) Directory.CreateDirectory(path, DirMode);
        File.SetUnixFileMode(path, DirMode);
        foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
        {
            try { File.SetUnixFileMode(entry, Directory.Exists(entry) ? DirMode : FileMode); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
        }
    }

    /// <summary>
    /// Restricts one file: 0600 on Unix. On Windows a file inside a secured directory just inherits its
    /// ACL; pass <paramref name="ownAcl"/> for a file in a folder the app doesn't own, to give the file
    /// itself an owner-only ACL without touching the folder.
    /// </summary>
    public static void RestrictFile(string path, bool ownAcl = false)
    {
        if (!File.Exists(path)) return;
        if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(path, FileMode); return; }
        if (ownAcl) RestrictFileWindows(path);
    }

    [SupportedOSPlatform("windows")]
    private static void RestrictFileWindows(string path)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(CurrentUser, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    /// <summary>True when the directory is already owner-only (used by tests and to skip rewriting the ACL).</summary>
    public static bool IsOwnerOnly(string path)
    {
        if (OperatingSystem.IsWindows()) return IsOwnerOnlyWindows(path);
        return (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                                              UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) == 0;
    }

    // properties, not static fields: a field initialiser would run on Linux too when the class loads
    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier SystemSid => new(WellKnownSidType.LocalSystemSid, null);

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier CurrentUser => WindowsIdentity.GetCurrent().User!;

    [SupportedOSPlatform("windows")]
    private static void SecureDirectoryWindows(string path)
    {
        if (IsOwnerOnlyWindows(path)) return;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false); // drop inherited entries
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(CurrentUser, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.SetOwner(CurrentUser);
        new DirectoryInfo(path).SetAccessControl(security); // children inherit the new entries
    }

    [SupportedOSPlatform("windows")]
    private static bool IsOwnerOnlyWindows(string path)
    {
        var acl = new DirectoryInfo(path).GetAccessControl();
        if (!acl.AreAccessRulesProtected) return false;
        var allowed = new[] { CurrentUser.Value, SystemSid.Value };
        return acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .All(r => allowed.Contains(r.IdentityReference.Value));
    }
}
