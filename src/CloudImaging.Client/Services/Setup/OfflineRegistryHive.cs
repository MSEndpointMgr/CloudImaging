using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using CloudImaging.Contracts.Enums;
using Microsoft.Win32;

namespace CloudImaging.Client.Services.Setup;

/// <summary>A hive of the applied image could not be mounted. The message is shown to operators.</summary>
public sealed class OfflineHiveException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Mounts the applied image's registry hives in WinPE and writes to them. One interface so the
/// Prepare setup step can be tested without a real image or registry.
/// </summary>
public interface IOfflineRegistryHive
{
    /// <summary>Mounts <paramref name="hiveFilePath"/> under HKLM. Throws <see cref="OfflineHiveException"/> when it cannot.</summary>
    void Load(SetupRegistryHive hive, string hiveFilePath);

    /// <summary>Unmounts the hive. False when it stayed mounted; the written values are already on disk either way.</summary>
    bool Unload(SetupRegistryHive hive);

    /// <summary>Creates the key when needed and sets the value.</summary>
    void SetValue(SetupRegistryHive hive, string key, string valueName, object value, RegistryValueKind kind);

    /// <summary>Reads a value, or null when the key or value does not exist.</summary>
    object? GetValue(SetupRegistryHive hive, string key, string valueName);

    /// <summary>Deletes a value when it exists.</summary>
    void DeleteValue(SetupRegistryHive hive, string key, string valueName);
}

/// <summary>
/// RegLoadKey and RegUnLoadKey over the applied image's hive files, with SeBackupPrivilege and
/// SeRestorePrivilege enabled. Every handle into a mounted hive is closed before it is unmounted,
/// because an open handle makes RegUnLoadKey fail. Disposing unmounts anything still mounted.
/// </summary>
public sealed class OfflineRegistryHive : IOfflineRegistryHive, IDisposable
{
    private readonly HashSet<SetupRegistryHive> _loaded = [];

    /// <inheritdoc/>
    public void Load(SetupRegistryHive hive, string hiveFilePath)
    {
        if (!File.Exists(hiveFilePath))
        {
            throw new OfflineHiveException(hive switch
            {
                SetupRegistryHive.DefaultUser => "The default user profile was not found in the applied image.",
                SetupRegistryHive.System => "The SYSTEM registry hive was not found in the applied image.",
                _ => "The SOFTWARE registry hive was not found in the applied image.",
            });
        }

        // Loading a hive is a backup and restore operation; WinPE's SYSTEM token holds both
        // privileges but, like every privilege, they are disabled until asked for.
        EnablePrivilegeOrThrow(SE_BACKUP_NAME);
        EnablePrivilegeOrThrow(SE_RESTORE_NAME);

        // A previous attempt that failed after loading would otherwise make RegLoadKey fail with
        // ERROR_SHARING_VIOLATION for the rest of this WinPE session.
        var mountName = OfflineRegistryPath.MountName(hive);
        _ = RegUnLoadKey(HKEY_LOCAL_MACHINE, mountName);

        var status = RegLoadKey(HKEY_LOCAL_MACHINE, mountName, hiveFilePath);
        if (status != ERROR_SUCCESS)
        {
            throw new OfflineHiveException($"The registry hive {hiveFilePath} could not be loaded (error {status}).", new Win32Exception(status));
        }
        _loaded.Add(hive);
    }

    /// <inheritdoc/>
    public bool Unload(SetupRegistryHive hive)
    {
        var mountName = OfflineRegistryPath.MountName(hive);
        var status = RegUnLoadKey(HKEY_LOCAL_MACHINE, mountName);
        if (status != ERROR_SUCCESS)
        {
            // .NET can keep a RegistryKey's native handle alive until it is finalized, which keeps
            // the hive open and fails the unload. Force the finalizers through and try once more.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            status = RegUnLoadKey(HKEY_LOCAL_MACHINE, mountName);
        }
        _loaded.Remove(hive);
        return status == ERROR_SUCCESS;
    }

    /// <summary>Opens the mounted hive's root key. The caller must dispose it before <see cref="Unload"/>.</summary>
    public RegistryKey OpenRoot(SetupRegistryHive hive, bool writable) =>
        (_loaded.Contains(hive) ? Registry.LocalMachine.OpenSubKey(OfflineRegistryPath.MountName(hive), writable) : null)
            ?? throw new OfflineHiveException($"The mounted hive {OfflineRegistryPath.MountRoot(hive)} could not be opened.");

    /// <inheritdoc/>
    public void SetValue(SetupRegistryHive hive, string key, string valueName, object value, RegistryValueKind kind)
    {
        using var root = OpenRoot(hive, writable: true);
        using var target = root.CreateSubKey(key, writable: true);
        target.SetValue(valueName, value, kind);
    }

    /// <inheritdoc/>
    public object? GetValue(SetupRegistryHive hive, string key, string valueName)
    {
        using var root = OpenRoot(hive, writable: false);
        using var target = root.OpenSubKey(key);
        return target?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }

    /// <inheritdoc/>
    public void DeleteValue(SetupRegistryHive hive, string key, string valueName)
    {
        using var root = OpenRoot(hive, writable: true);
        using var target = root.OpenSubKey(key, writable: true);
        target?.DeleteValue(valueName, throwOnMissingValue: false);
    }

    /// <summary>Unmounts any hive a failed step left mounted.</summary>
    public void Dispose()
    {
        foreach (var hive in _loaded.ToList())
        {
            Unload(hive);
        }
    }

    // ── Win32 interop for RegLoadKey/RegUnLoadKey + SeBackup/SeRestorePrivilege ──────────────

    private static readonly IntPtr HKEY_LOCAL_MACHINE = new(unchecked((int)0x80000002));

    private const int ERROR_SUCCESS = 0;
    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint SE_PRIVILEGE_ENABLED = 0x0002;
    private const string SE_BACKUP_NAME = "SeBackupPrivilege";
    private const string SE_RESTORE_NAME = "SeRestorePrivilege";

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    private static void EnablePrivilegeOrThrow(string privilegeName)
    {
        if (!EnablePrivilege(privilegeName))
        {
            throw new OfflineHiveException($"{privilegeName} could not be enabled, so the applied image's registry hives cannot be loaded.");
        }
    }

    private static bool EnablePrivilege(string privilegeName)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var tokenHandle))
        {
            return false;
        }

        try
        {
            if (!LookupPrivilegeValue(null, privilegeName, out var luid))
            {
                return false;
            }

            var privileges = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SE_PRIVILEGE_ENABLED,
            };

            // AdjustTokenPrivileges reports success even when it only adjusted some of the
            // requested privileges, so the last error has to be checked as well.
            return AdjustTokenPrivileges(tokenHandle, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero)
                && Marshal.GetLastWin32Error() == ERROR_SUCCESS;
        }
        finally
        {
            CloseHandle(tokenHandle);
        }
    }

    [DllImport("advapi32.dll", EntryPoint = "RegLoadKeyW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int RegLoadKey(IntPtr hKey, string lpSubKey, string lpFile);

    [DllImport("advapi32.dll", EntryPoint = "RegUnLoadKeyW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int RegUnLoadKey(IntPtr hKey, string lpSubKey);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr tokenHandle,
        bool disableAllPrivileges,
        ref TOKEN_PRIVILEGES newState,
        uint bufferLengthInBytes,
        IntPtr previousState,
        IntPtr returnLengthInBytes);
}
