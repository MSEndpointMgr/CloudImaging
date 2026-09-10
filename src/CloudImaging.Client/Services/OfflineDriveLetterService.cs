using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace CloudImaging.Client.Services;

/// <summary>
/// Makes the freshly imaged Windows partition come up as <c>C:</c> on first boot, regardless of
/// which drive letter it happened to be mounted under while WinPE was imaging it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is needed.</b> <see cref="DiskFormatService"/> assigns the target partitions the
/// first free drive letters WinPE has left (the boot media, its cache partition and any attached
/// USB storage have usually taken several already), so the Windows volume is routinely mounted as
/// something like <c>H:</c>. Everything the pipeline does afterwards — DISM apply, bcdboot,
/// reagentc — is addressed through that letter. The letter itself is a WinPE-only artifact and
/// normally has no bearing on the deployed OS, but the drive letter the deployed OS ends up using
/// is <i>not</i> derived from the disk layout: at boot, mountmgr reads the letter assignments
/// stored in the SYSTEM hive it is booting from, under <c>HKLM\SYSTEM\MountedDevices</c>.
/// </para>
/// <para>
/// A stock, Sysprep-generalized <c>install.wim</c> ships with those assignments cleared, so it
/// lands on <c>C:</c> by luck rather than by design. A custom-captured image — exactly what this
/// product lets an administrator upload — very often still carries the letter mappings of the
/// machine it was captured from, and a machine that boots with Windows on anything other than
/// <c>C:</c> is effectively broken: hard-coded paths, installers, Group Policy, servicing and
/// most in-box tooling all assume <c>C:</c>, and the letter cannot be changed afterwards.
/// </para>
/// <para>
/// <b>The fix</b> is the same one MDT (<c>LTIApply</c>) and Configuration Manager OSD
/// (<c>OSDPreserveDriveLetter=False</c>) apply after laying the image down: mount the applied
/// image's own SYSTEM hive offline, throw away every inherited <c>\DosDevices\&lt;letter&gt;:</c>
/// mapping, and write a single new one pointing <c>C:</c> at the volume that was just imaged. The
/// binary volume identity that value needs is not something this code has to construct — WinPE's
/// own mountmgr already recorded it under the live <c>HKLM\SYSTEM\MountedDevices</c> when diskpart
/// assigned the partition its temporary letter, so it is simply copied across.
/// </para>
/// <para>
/// Runs in the ConfigureBoot step, after DISM has laid down <c>\Windows\System32\config\SYSTEM</c>
/// and before <c>bcdboot</c>. bcdboot, reagentc and the BCD itself all address partitions by
/// device/GUID rather than by drive letter, so re-pointing the letters does not disturb them.
/// </para>
/// </remarks>
public sealed partial class OfflineDriveLetterService
{
    /// <summary>Live WinPE mount-manager state — the source of the target volume's binary identity.</summary>
    private const string LiveMountedDevicesKey = @"SYSTEM\MountedDevices";

    /// <summary>Key name the applied image's SYSTEM hive is temporarily mounted under, beneath HKLM.</summary>
    private const string OfflineHiveKeyName = "CI_OFFLINE_SYSTEM";

    /// <summary>Sub-key of the SYSTEM hive root that holds mountmgr's persisted letter assignments.</summary>
    private const string MountedDevicesSubKey = "MountedDevices";

    private const string DosDevicePrefix = @"\DosDevices\";

    /// <summary>The drive letter every Windows installation is expected to boot as.</summary>
    private const string TargetDosDeviceValue = @"\DosDevices\C:";

    private readonly ILogger<OfflineDriveLetterService> _logger;

    public OfflineDriveLetterService(ILogger<OfflineDriveLetterService> logger) => _logger = logger;

    /// <summary>
    /// Rewrites the applied image's offline <c>MountedDevices</c> key so that the volume currently
    /// mounted as <paramref name="windowsVolume"/> becomes <c>C:</c> once the device boots into
    /// Windows.
    /// </summary>
    /// <param name="windowsVolume">The Windows partition's WinPE drive letter, e.g. <c>"H:"</c>.</param>
    /// <exception cref="InvalidOperationException">
    /// The applied image has no SYSTEM hive, the required privileges could not be acquired, or the
    /// hive could not be mounted.
    /// </exception>
    public Task EnsureWindowsVolumeBootsAsCAsync(string windowsVolume, CancellationToken ct = default)
        // Mounting and rewriting a multi-hundred-megabyte SYSTEM hive is synchronous registry I/O
        // against the disk that has just been written to, so it is kept off the UI thread rather
        // than freezing ProgressView's ticker/ring for the duration.
        => Task.Run(() => EnsureWindowsVolumeBootsAsC(windowsVolume), ct);

    private void EnsureWindowsVolumeBootsAsC(string windowsVolume)
    {
        WinPeEnvironmentGuard.EnsureRunningInWinPe("Setting the deployed operating system's drive letter");

        var letter = char.ToUpperInvariant(windowsVolume.Trim()[0]);
        var hivePath = Path.Combine($"{letter}:\\", "Windows", "System32", "config", "SYSTEM");

        if (!File.Exists(hivePath))
        {
            throw new InvalidOperationException(
                $"\"{hivePath}\" was not found, so the deployed operating system's drive letter could " +
                "not be set. The OS image does not appear to have been applied successfully.");
        }

        // mountmgr wrote this when diskpart assigned the partition its temporary letter. It is the
        // opaque identity ("DMIO:ID:" + the GPT partition GUID) that a MountedDevices value must
        // contain for a letter to bind to this exact partition.
        var volumeId = ReadLiveVolumeIdentity(letter);
        if (volumeId is null or { Length: 0 })
        {
            // Without the identity the letter cannot be pinned, but clearing the inherited
            // mappings on its own still leaves mountmgr free to assign the boot volume C: — which
            // is what it does when nothing else claims the letter. Worth doing, worth flagging.
            LogVolumeIdentityUnavailable(_logger, letter);
        }

        // Loading a hive is a backup/restore operation; both privileges are held by the WinPE
        // SYSTEM context but, like every privilege, are disabled in the token until asked for.
        EnablePrivilegeOrThrow(SE_BACKUP_NAME);
        EnablePrivilegeOrThrow(SE_RESTORE_NAME);

        // Defensive: a previous attempt that failed after loading would otherwise make RegLoadKey
        // fail with ERROR_SHARING_VIOLATION for the rest of this WinPE session.
        _ = RegUnLoadKey(HKEY_LOCAL_MACHINE, OfflineHiveKeyName);

        LogLoadingOfflineHive(_logger, hivePath);
        var loadStatus = RegLoadKey(HKEY_LOCAL_MACHINE, OfflineHiveKeyName, hivePath);
        if (loadStatus != ERROR_SUCCESS)
        {
            throw new InvalidOperationException(
                $"The applied image's registry hive \"{hivePath}\" could not be mounted (error {loadStatus}).",
                new Win32Exception(loadStatus));
        }

        try
        {
            var clearedMappings = RewriteMountedDevices(volumeId);
            LogDriveLetterAssigned(_logger, letter, clearedMappings);
        }
        finally
        {
            UnloadOfflineHive();
        }
    }

    /// <summary>
    /// Deletes every inherited drive-letter mapping from the offline hive and, when the target
    /// volume's identity is known, pins <c>C:</c> to it. Returns the number of mappings removed.
    /// </summary>
    private static int RewriteMountedDevices(byte[]? volumeId)
    {
        // Scoped tightly: every handle into the loaded hive must be closed before RegUnLoadKey can
        // succeed, so nothing here may outlive this method.
        using var hive = Registry.LocalMachine.OpenSubKey(OfflineHiveKeyName, writable: true)
            ?? throw new InvalidOperationException(
                $"The mounted offline hive \"{OfflineHiveKeyName}\" could not be opened for writing.");

        using var mountedDevices = hive.OpenSubKey(MountedDevicesSubKey, writable: true)
            ?? hive.CreateSubKey(MountedDevicesSubKey, writable: true);

        var cleared = 0;
        foreach (var valueName in mountedDevices.GetValueNames())
        {
            // Only the \DosDevices\X: values decide drive letters. The \??\Volume{...} values
            // alongside them are volume-GUID mappings for volumes that do not exist on this
            // device; they bind nothing and are left alone.
            if (valueName.StartsWith(DosDevicePrefix, StringComparison.OrdinalIgnoreCase))
            {
                mountedDevices.DeleteValue(valueName, throwOnMissingValue: false);
                cleared++;
            }
        }

        if (volumeId is { Length: > 0 })
        {
            mountedDevices.SetValue(TargetDosDeviceValue, volumeId, RegistryValueKind.Binary);
        }

        return cleared;
    }

    private static byte[]? ReadLiveVolumeIdentity(char letter)
    {
        using var mountedDevices = Registry.LocalMachine.OpenSubKey(LiveMountedDevicesKey);
        return mountedDevices?.GetValue($@"{DosDevicePrefix}{letter}:") as byte[];
    }

    private void UnloadOfflineHive()
    {
        var status = RegUnLoadKey(HKEY_LOCAL_MACHINE, OfflineHiveKeyName);
        if (status == ERROR_SUCCESS)
        {
            return;
        }

        // .NET can keep a RegistryKey's native handle alive until it is finalized, which keeps the
        // hive open and fails the unload. Force the finalizers through and try once more.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        status = RegUnLoadKey(HKEY_LOCAL_MACHINE, OfflineHiveKeyName);

        if (status != ERROR_SUCCESS)
        {
            // Non-fatal: RegLoadKey writes changes straight through to the hive file, so the new
            // mapping is already persisted. The device restarts moments later, which releases the
            // handle regardless.
            LogOfflineHiveUnloadFailed(_logger, status);
        }
    }

    // ── Logging ─────────────────────────────────────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Information, Message = "Mounting the applied image's registry hive {HivePath} to set the deployed drive letter.")]
    private static partial void LogLoadingOfflineHive(ILogger logger, string hivePath);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "No mount-manager identity was found for volume {Letter}: — inherited drive-letter mappings will be cleared, but C: cannot be pinned explicitly.")]
    private static partial void LogVolumeIdentityUnavailable(ILogger logger, char letter);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Deployed Windows volume (WinPE {Letter}:) will boot as C:. Cleared {ClearedMappings} inherited drive-letter mapping(s).")]
    private static partial void LogDriveLetterAssigned(ILogger logger, char letter, int clearedMappings);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "The offline registry hive could not be unmounted (error {Status}). The drive-letter change is already written; the pending restart releases the hive.")]
    private static partial void LogOfflineHiveUnloadFailed(ILogger logger, int status);

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
            throw new InvalidOperationException(
                $"{privilegeName} could not be enabled, so the applied image's registry hive cannot be " +
                "mounted to set the deployed operating system's drive letter.");
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
