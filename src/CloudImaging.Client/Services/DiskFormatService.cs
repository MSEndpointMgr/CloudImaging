using System.Diagnostics;
using System.IO;
using System.Management;
using Microsoft.Extensions.Logging;

namespace CloudImaging.Client.Services;

/// <summary>
/// Formats the target disk before the OS image is applied — the "Format" step of the
/// Format/Download/Apply pipeline (FR-007).
///
/// Target disk selection: the single non-removable (non-USB-interface) disk attached to the
/// device is selected automatically. If zero or more than one such disk is found, formatting
/// is refused with a clear error rather than guessing which physical disk to wipe — there is
/// no spec-defined disk-selection policy for multi-disk devices today; this is a deliberately
/// conservative default pending a product decision for that scenario.
///
/// Produces a single GPT partition spanning the disk, quick-formatted NTFS, assigned the next
/// available drive letter. This is the minimal layout required by <see cref="ImageApplyService"/>'s
/// DISM /Apply-Image call. It does NOT create an EFI System Partition/MSR partition or run
/// bcdboot — full UEFI boot-configuration partitioning is out of scope of this change and is
/// called out here explicitly so this simplification is never mistaken for a complete
/// implementation.
/// </summary>
public sealed partial class DiskFormatService
{
    private readonly ILogger<DiskFormatService> _logger;

    public DiskFormatService(ILogger<DiskFormatService> logger) => _logger = logger;

    /// <summary>
    /// Cleans, partitions, and formats the sole eligible fixed disk, returning the assigned
    /// drive letter (e.g. <c>"C:"</c>) for use as the DISM apply target.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Zero or multiple eligible fixed disks were found, or diskpart failed.
    /// </exception>
    public async Task<string> FormatTargetDiskAsync(CancellationToken ct = default)
    {
        var diskIndex = FindSingleFixedDiskIndex();
        LogTargetDiskSelected(_logger, diskIndex);

        var driveLetter = FindNextAvailableDriveLetter();
        var script =
            $"select disk {diskIndex}\r\n" +
            "clean\r\n" +
            "convert gpt\r\n" +
            "create partition primary\r\n" +
            "format fs=ntfs quick label=\"Windows\"\r\n" +
            $"assign letter={driveLetter}\r\n";

        var scriptPath = Path.Combine(Path.GetTempPath(), $"ci-diskpart-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(scriptPath, script, ct);

        try
        {
            await RunDiskpartAsync(scriptPath, ct);
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { /* best-effort cleanup */ }
        }

        LogFormatCompleted(_logger, diskIndex, driveLetter);
        return $"{driveLetter}:";
    }

    /// <summary>
    /// Returns the WMI disk index of the sole non-USB disk attached to the device.
    /// Throws if zero or more than one candidate is found — automatic selection is refused
    /// rather than risking formatting the wrong disk.
    /// </summary>
    private static int FindSingleFixedDiskIndex()
    {
        var candidates = new List<int>();
        using var searcher = new ManagementObjectSearcher("SELECT Index, InterfaceType FROM Win32_DiskDrive");
        using var results = searcher.Get();
        foreach (ManagementObject disk in results)
        {
            var interfaceType = disk["InterfaceType"]?.ToString();
            if (!string.Equals(interfaceType, "USB", StringComparison.OrdinalIgnoreCase) && disk["Index"] is not null)
            {
                candidates.Add((int)(uint)disk["Index"]);
            }
        }

        return candidates.Count switch
        {
            0 => throw new InvalidOperationException(
                "No fixed (non-USB) disk was found on this device. Formatting was refused."),
            1 => candidates[0],
            _ => throw new InvalidOperationException(
                $"{candidates.Count} fixed disks were found on this device. Automatic target-disk " +
                "selection requires exactly one; formatting was refused to avoid wiping the wrong disk."),
        };
    }

    private static string FindNextAvailableDriveLetter()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        for (var c = 'C'; c <= 'Z'; c++)
        {
            if (!used.Contains(c)) return c.ToString();
        }

        throw new InvalidOperationException("No available drive letter could be found.");
    }

    private async Task RunDiskpartAsync(string scriptPath, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "diskpart.exe",
                Arguments = $"/s \"{scriptPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };

        process.Start();
        var stdOut = await process.StandardOutput.ReadToEndAsync(ct);
        var stdErr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            LogDiskpartFailed(_logger, process.ExitCode, stdOut + stdErr);
            throw new InvalidOperationException($"diskpart exited with code {process.ExitCode}: {stdOut}{stdErr}");
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Target disk selected for formatting: disk {DiskIndex}.")]
    private static partial void LogTargetDiskSelected(ILogger logger, int diskIndex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Disk {DiskIndex} formatted and assigned drive {DriveLetter}:.")]
    private static partial void LogFormatCompleted(ILogger logger, int diskIndex, string driveLetter);

    [LoggerMessage(Level = LogLevel.Error, Message = "diskpart failed with exit code {ExitCode}. Output: {Output}")]
    private static partial void LogDiskpartFailed(ILogger logger, int exitCode, string output);
}
