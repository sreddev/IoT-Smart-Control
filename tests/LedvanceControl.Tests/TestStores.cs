using LedvanceControl.Core.Model;
using LedvanceControl.Core.Security;

namespace LedvanceControl.Tests;

/// <summary>
/// Config stores for tests that never touch the machine's real keyring: Windows uses DPAPI (silent);
/// Linux uses an in-memory master key shared by the whole test run, so reloading a config decrypts —
/// and no keyring unlock prompt can appear on the desktop. Only the opt-in Linux integration tests use
/// the real stores.
/// </summary>
internal static class TestStores
{
    private static readonly MemoryKeySource SharedKey = new();

    public static ISecretProtector Protector =>
        OperatingSystem.IsWindows() ? new DpapiProtector() : new MasterKeyProtector(SharedKey);

    public static ConfigStore Create(string path) => new(path, Protector);
}
