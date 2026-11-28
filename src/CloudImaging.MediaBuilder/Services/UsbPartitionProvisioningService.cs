using System.IO;
using System.Management;
using Microsoft.Extensions.Logging;

namespace CloudImaging.MediaBuilder.Services;

/// <summary>
/// Provisions a USB drive with two partitions for WinPE boot image deployment (T070, T071, FR-055).
///
/// Partition layout:
///   Part 1 (FAT32, boot, minimum 2 GB):   WinPE boot files.
///   Part 2 (NTFS, cache, the rest):       Cloud Imaging Client OS image cache
///                                         (see CloudImaging.Client's ImageCacheService, FR-009d).
/// </summary>
public sealed partial class UsbPartitionProvisioningService
{
    /// <summary>Boot partition size in MB — the FR-055 minimum is 2 GB (2048 MB).</summary>
    public const int BootPartitionSizeMb = 2048;

    /// <summary>Smallest accepted USB device. Sticks sold as 8 GB report roughly 7.2 to 8 GB of usable capacity.</summary>
    public const long MinimumDiskBytes = 7_000_000_000L;

    /// <summary>Label for <see cref="MinimumDiskBytes"/> in messages, matching how USB devices are sold.</summary>
    public const string MinimumDiskLabel = "8 GB";

    /// <summary>
    /// Cache partition size that holds one OS image at the top of the supported range (20 GB) plus
    /// NTFS headroom. Smaller devices still work: the Client stages images that don't fit on the
    /// target disk instead, so they are downloaded every time rather than cached.
    /// </summary>
    public const long RecommendedCachePartitionBytes = 24L * 1024 * 1024 * 1024;

    /// <summary>Size left for the cache partition on a disk of <paramref name="diskSizeBytes"/>.</summary>
    public static long CachePartitionBytes(long diskSizeBytes) => diskSizeBytes - BootPartitionSizeMb * 1024L * 1024L;

    /// <summary>A volume on a disk.</summary>
    /// <param name="DriveLetter">Drive letter with its colon, for example <c>E:</c>.</param>
    /// <param name="Label">Volume label; null when it has none.</param>
    public sealed record DiskVolume(string DriveLetter, string? Label);

    private readonly ILogger<UsbPartitionProvisioningService> _logger;
    private readonly Func<uint, IReadOnlyList<DiskVolume>> _volumesOnDisk;

    /// <summary>Builds the service over the given logger.</summary>
    /// <param name="logger">Logger.</param>
    /// <param name="volumesOnDiskOverride">Test seam. Defaults to the volumes Windows reports on the disk.</param>
    public UsbPartitionProvisioningService(ILogger<UsbPartitionProvisioningService> logger, Func<uint, IReadOnlyList<DiskVolume>>? volumesOnDiskOverride = null)
    {
        _logger = logger;
        _volumesOnDisk = volumesOnDiskOverride ?? VolumesOnDisk;
    }

    /// <summary>
    /// Provisions the USB disk at <paramref name="diskNumber"/> with the WinPE partition layout.
    /// <b>DESTRUCTIVE</b> — all existing data on the disk is erased.
    /// </summary>
    /// <param name="diskNumber">Physical disk index to partition.</param>
    /// <param name="diskSizeBytes">
    /// Total disk capacity. Checked against <see cref="MinimumDiskBytes"/> before any destructive
    /// action is taken.
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
    /// <param name="ct">Cancellation token to abort provisioning.</param>
    public async Task ProvisionAsync(
        uint diskNumber,
        long diskSizeBytes,
        Action<string, int>? onProgress = null,
        string? diskLabel = null,
        CancellationToken ct = default)
    {
        if (diskSizeBytes < MinimumDiskBytes)
        {
            throw new InvalidOperationException(
                $"This USB device is too small ({diskSizeBytes / (1024.0 * 1024 * 1024):0.0} GB). Use a USB device of at least {MinimumDiskLabel}.");
        }

        var label = string.IsNullOrWhiteSpace(diskLabel) ? $"disk {diskNumber}" : diskLabel;
        LogStarting(_logger, diskNumber);
        using var formatPromptPause = await ShellHardwareDetectionPause.BeginAsync(_logger);

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
    /// Returns the drive letter (e.g. <c>"E:"</c>) of the FAT32 volume labelled <c>BOOT</c> that
    /// <see cref="ProvisionAsync"/> created on disk <paramref name="diskNumber"/>, or <c>null</c> if
    /// it cannot be located. Used to hand the boot partition off to the deployment step. Only that
    /// disk is searched: any other prepared USB device plugged in has a BOOT volume of its own.
    /// </summary>
    public string? FindBootVolumeDriveLetter(uint diskNumber) => FindVolumeDriveLetter(diskNumber, "BOOT");

    /// <summary>
    /// Returns the drive letter of the NTFS volume labelled <c>CACHE</c> that
    /// <see cref="ProvisionAsync"/> created on disk <paramref name="diskNumber"/>, or <c>null</c> if
    /// it cannot be located. Preloaded driver files are copied there, and the letter is recorded in
    /// <c>UsbPreparationManifest.PartitionSchema</c> (T071a, FR-059). Only that disk is searched.
    /// </summary>
    public string? FindCacheVolumeDriveLetter(uint diskNumber) => FindVolumeDriveLetter(diskNumber, "CACHE");

    private string? FindVolumeDriveLetter(uint diskNumber, string label) =>
        _volumesOnDisk(diskNumber).FirstOrDefault(volume => string.Equals(volume.Label, label, StringComparison.OrdinalIgnoreCase))?.DriveLetter;

    /// <summary>
    /// The lettered volumes on disk <paramref name="diskNumber"/>, partition to volume: the reverse
    /// of the trace <c>UsbSafetyValidationService</c> uses to find the system disk. Empty when the
    /// lookup fails.
    /// </summary>
    internal IReadOnlyList<DiskVolume> VolumesOnDisk(uint diskNumber)
    {
        var volumes = new List<DiskVolume>();
        try
        {
            using var partitionSearcher = new ManagementObjectSearcher(
                $"SELECT DeviceID FROM Win32_DiskPartition WHERE DiskIndex = {diskNumber}");
            using var partitions = partitionSearcher.Get();
            foreach (ManagementObject partition in partitions)
            {
                using var volumeSearcher = new ManagementObjectSearcher(
                    $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partition["DeviceID"]}'}} WHERE AssocClass = Win32_LogicalDiskToPartition");
                using var logicalDisks = volumeSearcher.Get();
                foreach (ManagementObject logicalDisk in logicalDisks)
                {
                    if (logicalDisk["DeviceID"]?.ToString() is { Length: > 0 } letter)
                    {
                        volumes.Add(new DiskVolume(letter, logicalDisk["VolumeName"]?.ToString()));
                    }
                }
            }
        }
        catch (ManagementException ex)
        {
            LogVolumeLookupFailed(_logger, ex, diskNumber);
        }

        return volumes;
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
    /// <remarks><c>override</c> dismounts the new volume first if Explorer already has it open.</remarks>
    private static string BuildBootPartitionScript(uint diskNumber) =>
        $"""
         select disk {diskNumber}
         create partition primary size={BootPartitionSizeMb}
         format quick fs=fat32 label="BOOT" override
         active
         assign
         exit
         """;

    /// <summary>Stage 3: create, format (NTFS), and assign the OS image cache partition.</summary>
    private static string BuildCachePartitionScript(uint diskNumber) =>
        $"""
         select disk {diskNumber}
         create partition primary
         format quick fs=ntfs label="CACHE" override
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the volumes on disk {DiskNumber} after provisioning.")]
    private static partial void LogVolumeLookupFailed(ILogger logger, Exception ex, uint diskNumber);
}
