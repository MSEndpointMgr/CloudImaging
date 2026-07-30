using System.IO;
using System.Management;
using Microsoft.Extensions.Logging;

namespace CloudImaging.MediaBuilder.Services;

/// <summary>
/// Provisions a USB drive with two partitions for WinPE boot image deployment (T070, T071, FR-055).
///
/// Partition layout:
///   Part 1 (FAT32, ~500 MB, bootable): WinPE boot files
///   Part 2 (NTFS, remainder):          Data partition (optional, unused by Cloud Imaging)
/// </summary>
public sealed partial class UsbPartitionProvisioningService
{
    private readonly ILogger<UsbPartitionProvisioningService> _logger;

    public UsbPartitionProvisioningService(ILogger<UsbPartitionProvisioningService> logger)
        => _logger = logger;

    /// <summary>
    /// Provisions the USB disk at <paramref name="diskNumber"/> with the WinPE partition layout.
    /// <b>DESTRUCTIVE</b> — all existing data on the disk is erased.
    /// </summary>
    public async Task ProvisionAsync(
        uint diskNumber,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        LogStarting(_logger, diskNumber);
        onProgress?.Invoke($"Partitioning disk {diskNumber}…");

        // Build a diskpart script for two-partition layout
        var script = BuildDiskpartScript(diskNumber);
        var scriptPath = Path.Combine(Path.GetTempPath(), $"ci-diskpart-{Guid.NewGuid():N}.txt");

        try
        {
            await File.WriteAllTextAsync(scriptPath, script, ct);
            await RunDiskpartAsync(scriptPath, ct);
            onProgress?.Invoke("Disk partitioned successfully.");
            LogComplete(_logger, diskNumber);
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
    public string? FindBootVolumeDriveLetter()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DriveLetter, Label FROM Win32_Volume WHERE Label='BOOT'");
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

    private static string BuildDiskpartScript(uint diskNumber) =>
        $"""
         select disk {diskNumber}
         clean
         convert mbr
         create partition primary size=500
         format quick fs=fat32 label="BOOT"
         active
         assign
         create partition primary
         format quick fs=ntfs label="DATA"
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
