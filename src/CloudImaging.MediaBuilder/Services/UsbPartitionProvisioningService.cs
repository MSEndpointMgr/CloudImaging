using System.IO;
using System.Management;
using Microsoft.Extensions.Logging;

namespace CloudImaging.MediaBuilder.Services;

/// <summary>
/// Provisions a USB drive with two partitions for WinPE boot image deployment (T070, T071, FR-055).
///
/// Partition layout:
///   Part 1 (FAT32, boot, minimum 2 GB):   WinPE boot files.
///   Part 2 (NTFS, cache, minimum 20 GB):  Cloud Imaging Client OS image cache
///                                         (see CloudImaging.Client's ImageCacheService, FR-009d).
/// </summary>
public sealed partial class UsbPartitionProvisioningService
{
    /// <summary>Boot partition size in MB — the FR-055 minimum is 2 GB (2048 MB).</summary>
    public const int BootPartitionSizeMb = 2048;

    /// <summary>Minimum cache partition size in bytes, per FR-055.</summary>
    public const long MinimumCachePartitionBytes = 20L * 1024 * 1024 * 1024;

    private readonly ILogger<UsbPartitionProvisioningService> _logger;

    public UsbPartitionProvisioningService(ILogger<UsbPartitionProvisioningService> logger)
        => _logger = logger;

    /// <summary>
    /// Provisions the USB disk at <paramref name="diskNumber"/> with the WinPE partition layout.
    /// <b>DESTRUCTIVE</b> — all existing data on the disk is erased.
    /// </summary>
    /// <param name="diskNumber">Physical disk index to partition.</param>
    /// <param name="diskSizeBytes">
    /// Total disk capacity. Used to verify — before any destructive action is taken — that the
    /// resulting cache partition will meet the <see cref="MinimumCachePartitionBytes"/> minimum
    /// required by FR-055.
    /// </param>
    /// <param name="onProgress">
    /// Reports a friendly message plus a 0-100 percent completion of just this partitioning
    /// step (the caller remaps that into its own overall-progress band). Three distinct stages
    /// are reported — erase/initialize, boot partition, cache partition — instead of a single
    /// flat "partitioning…" message, so the UI can show real sub-step detail.
    /// </param>
    /// <param name="diskLabel">
    /// Friendly display name (e.g. the disk's <c>Caption</c>, "SanDisk Extreme Pro USB Device")
    /// to use in progress messages instead of the bare disk number. Falls back to "disk N" when
    /// not supplied.
    /// </param>
    public async Task ProvisionAsync(
        uint diskNumber,
        long diskSizeBytes,
        Action<string, int>? onProgress = null,
        string? diskLabel = null,
        CancellationToken ct = default)
    {
        var bootPartitionBytes = BootPartitionSizeMb * 1024L * 1024L;
        var cachePartitionBytes = diskSizeBytes - bootPartitionBytes;
        if (cachePartitionBytes < MinimumCachePartitionBytes)
        {
            throw new InvalidOperationException(
                $"Selected USB device is too small: after the {BootPartitionSizeMb} MB boot partition, only " +
                $"{cachePartitionBytes / (1024.0 * 1024 * 1024):0.0} GB would remain for the cache partition, " +
                $"below the {MinimumCachePartitionBytes / (1024L * 1024 * 1024)} GB minimum required (FR-055). " +
                "Use a larger USB device.");
        }

        var label = string.IsNullOrWhiteSpace(diskLabel) ? $"disk {diskNumber}" : diskLabel;
        LogStarting(_logger, diskNumber);

        onProgress?.Invoke($"Erasing and initializing {label}…", 0);
        await RunDiskpartScriptAsync(BuildCleanAndConvertScript(diskNumber), ct);

        ct.ThrowIfCancellationRequested();
        onProgress?.Invoke($"Creating boot partition (FAT32) on {label}…", 35);
        await RunDiskpartScriptAsync(BuildBootPartitionScript(diskNumber), ct);

        ct.ThrowIfCancellationRequested();
        onProgress?.Invoke($"Creating cache partition (NTFS) on {label}…", 70);
        await RunDiskpartScriptAsync(BuildCachePartitionScript(diskNumber), ct);

        onProgress?.Invoke($"{label} partitioned successfully.", 100);
        LogComplete(_logger, diskNumber);
    }

    /// <summary>Writes <paramref name="script"/> to a temp file and runs it via diskpart.exe.</summary>
    private static async Task RunDiskpartScriptAsync(string script, CancellationToken ct)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"ci-diskpart-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(scriptPath, script, ct);
            await RunDiskpartAsync(scriptPath, ct);
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Returns the drive letter (e.g. <c>"E:"</c>) of the FAT32 volume labelled <c>BOOT</c>
    /// created by <see cref="ProvisionAsync"/>, or <c>null</c> if it cannot be located.
    /// Used to hand the boot partition off to the deployment step.
    /// </summary>
    public string? FindBootVolumeDriveLetter() => FindVolumeDriveLetterByLabel("BOOT");

    /// <summary>
    /// Returns the drive letter of the NTFS volume labelled <c>CACHE</c> created by
    /// <see cref="ProvisionAsync"/>, or <c>null</c> if it cannot be located. Used only to
    /// record the partition layout in <c>UsbPreparationManifest.PartitionSchema</c> (T071a,
    /// FR-059) — the manifest itself is written to the BOOT partition, not here.
    /// </summary>
    public string? FindCacheVolumeDriveLetter() => FindVolumeDriveLetterByLabel("CACHE");

    private string? FindVolumeDriveLetterByLabel(string label)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT DriveLetter, Label FROM Win32_Volume WHERE Label='{label}'");
            using var results = searcher.Get();
            foreach (ManagementObject volume in results)
            {
                var letter = volume["DriveLetter"]?.ToString();
                if (!string.IsNullOrWhiteSpace(letter))
                    return letter;   // e.g. "E:"
            }
        }
        catch (ManagementException ex)
        {
            LogBootVolumeLookupFailed(_logger, ex);
        }
        return null;
    }

    /// <summary>Stage 1: wipe any existing partition table and lay down a fresh MBR.</summary>
    private static string BuildCleanAndConvertScript(uint diskNumber) =>
        $"""
         select disk {diskNumber}
         clean
         convert mbr
         exit
         """;

    /// <summary>Stage 2: create, format (FAT32), activate, and assign the WinPE boot partition.</summary>
    private static string BuildBootPartitionScript(uint diskNumber) =>
        $"""
         select disk {diskNumber}
         create partition primary size={BootPartitionSizeMb}
         format quick fs=fat32 label="BOOT"
         active
         assign
         exit
         """;

    /// <summary>Stage 3: create, format (NTFS), and assign the OS image cache partition.</summary>
    private static string BuildCachePartitionScript(uint diskNumber) =>
        $"""
         select disk {diskNumber}
         create partition primary
         format quick fs=ntfs label="CACHE"
         assign
         exit
         """;

    private static async Task RunDiskpartAsync(string scriptPath, CancellationToken ct)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName               = "diskpart.exe",
                Arguments              = $"/s \"{scriptPath}\"",
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            },
            EnableRaisingEvents = true,
        };
        var tcs = new TaskCompletionSource<int>();
        process.Exited += (_, _) => tcs.TrySetResult(process.ExitCode);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        ct.Register(() => { try { process.Kill(); } catch { } });
        int exitCode = await tcs.Task;
        if (exitCode != 0)
            throw new InvalidOperationException($"diskpart exited with code {exitCode}.");
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting USB partition provisioning for disk {DiskNumber}.")]
    private static partial void LogStarting(ILogger logger, uint diskNumber);

    [LoggerMessage(Level = LogLevel.Information, Message = "USB disk {DiskNumber} partitioned successfully.")]
    private static partial void LogComplete(ILogger logger, uint diskNumber);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not locate the BOOT volume drive letter after provisioning.")]
    private static partial void LogBootVolumeLookupFailed(ILogger logger, Exception ex);
}
