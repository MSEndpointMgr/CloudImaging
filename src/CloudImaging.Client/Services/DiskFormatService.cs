using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using Microsoft.Extensions.Logging;

namespace CloudImaging.Client.Services;

/// <summary>
/// The drive letters assigned to each formatted partition, for use by later pipeline steps
/// (DISM apply target, boot configuration, recovery image apply).
/// </summary>
public sealed record DiskFormatResult(string EfiSystemVolume, string WindowsVolume, string RecoveryVolume);

/// <summary>
/// Formats the target disk before the OS image is applied — the "Format" step of the imaging
/// pipeline (FR-007). Builds a full UEFI-bootable GPT layout (EFI System Partition, MSR,
/// Windows, Recovery) from the admin-configured <see cref="PartitioningScheme"/> snapshotted
/// onto the session, rather than a single non-bootable partition.
///
/// Target disk selection: the single non-removable (non-USB-interface) disk attached to the
/// device is selected automatically. If zero or more than one such disk is found, formatting
/// is refused with a clear error rather than guessing which physical disk to wipe — there is
/// no spec-defined disk-selection policy for multi-disk devices today; this is a deliberately
/// conservative default pending a product decision for that scenario.
/// </summary>
public sealed partial class DiskFormatService
{
    /// <summary>Minimum acceptable size for the Windows partition after other partitions are reserved.</summary>
    private const long MinWindowsPartitionMb = 8 * 1024;

    /// <summary>Safety margin subtracted from the disk's reported size to allow for GPT/alignment overhead.</summary>
    private const long SafetyMarginMb = 8;

    private readonly ILogger<DiskFormatService> _logger;

    public DiskFormatService(ILogger<DiskFormatService> logger) => _logger = logger;

    /// <summary>
    /// Cleans, partitions, and formats the sole eligible fixed disk according to
    /// <paramref name="scheme"/>, returning the drive letters assigned to each partition.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Zero or multiple eligible fixed disks were found, the scheme is missing a required
    /// partition type, the disk is too small for the configured sizes, or diskpart failed.
    /// </exception>
    public async Task<DiskFormatResult> FormatTargetDiskAsync(PartitioningScheme scheme, CancellationToken ct = default)
    {
        WinPeEnvironmentGuard.EnsureRunningInWinPe("Formatting the target disk");

        var ordered = ValidateAndOrder(scheme);

        var diskIndex = FindSingleFixedDiskIndex();
        LogTargetDiskSelected(_logger, diskIndex);

        var diskSizeMb = GetDiskSizeMb(diskIndex);
        var reservedMb = ordered.Where(p => p.PartitionType != PartitionType.Windows).Sum(p => (long)p.SizeMb);
        var windowsMb = diskSizeMb - reservedMb - SafetyMarginMb;

        if (windowsMb < MinWindowsPartitionMb)
        {
            throw new InvalidOperationException(
                $"The configured partitioning scheme leaves only {windowsMb} MB for the Windows partition " +
                $"on a {diskSizeMb} MB disk (minimum {MinWindowsPartitionMb} MB). Reduce the size of the " +
                "other partitions or use a larger disk.");
        }

        var letters = AllocateDriveLetters(3);
        var espLetter = letters[0];
        var windowsLetter = letters[1];
        var recoveryLetter = letters[2];

        var script = BuildDiskpartScript(diskIndex, ordered, windowsMb, espLetter, windowsLetter, recoveryLetter);

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

        LogFormatCompleted(_logger, diskIndex, windowsLetter);
        return new DiskFormatResult($"{espLetter}:", $"{windowsLetter}:", $"{recoveryLetter}:");
    }

    /// <summary>
    /// Validates that the scheme defines exactly one of each required, fixed partition type
    /// and returns the partitions ordered per their configured <see cref="PartitionDefinition.Order"/>.
    /// </summary>
    private static List<PartitionDefinition> ValidateAndOrder(PartitioningScheme scheme)
    {
        var ordered = scheme.Partitions.OrderBy(p => p.Order).ToList();
        var types = ordered.Select(p => p.PartitionType).ToHashSet();

        var required = new[] { PartitionType.EfiSystem, PartitionType.Msr, PartitionType.Windows, PartitionType.Recovery };
        if (required.Any(r => !types.Contains(r)) || types.Count != ordered.Count)
        {
            throw new InvalidOperationException(
                "The partitioning scheme must define exactly one each of EfiSystem, Msr, Windows, and Recovery.");
        }

        return ordered;
    }

    /// <summary>
    /// Builds the diskpart script for the full UEFI-bootable layout. The Windows partition
    /// always receives <paramref name="windowsSizeMb"/> (computed as the disk's remaining
    /// space after the other three fixed-size partitions) regardless of its position in
    /// <paramref name="ordered"/>, so the configured order never leaves a partition without
    /// its intended space. The Recovery partition is marked hidden/required via the GPT
    /// attribute bits (0x8000000000000001) but keeps a temporary drive letter so
    /// <see cref="RecoveryImageService"/> can write the WinRE image to it later in the pipeline.
    /// </summary>
    private static string BuildDiskpartScript(
        int diskIndex,
        IReadOnlyList<PartitionDefinition> ordered,
        long windowsSizeMb,
        string espLetter,
        string windowsLetter,
        string recoveryLetter)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"select disk {diskIndex}");
        sb.AppendLine("clean");
        sb.AppendLine("convert gpt");

        // WinPE's volume manager does not always finish re-enumerating the disk before diskpart
        // moves on to its next scripted command immediately after "clean"/"convert gpt" rewrite
        // the disk's partition table. Observed in the field as a partition being created
        // successfully (and even formatted) but a later command in the same script (assign/gpt
        // attributes) failing with exit code -2147024463 (0x800701B1 = HRESULT-wrapped Win32
        // error 433, ERROR_NO_SUCH_DEVICE) because Windows still has not mounted the volume it
        // just created. "rescan" forces diskpart to refresh its and Windows' view of all disks
        // and volumes before any partition-level command runs, closing that race.
        sb.AppendLine("rescan");

        // "rescan" does not reliably preserve diskpart's "selected disk" context (community and
        // in-house testing both show it can drop back to no selection), every command below
        // implicitly targets whatever disk is currently selected, so it must be reselected
        // explicitly or "create partition" could silently target the wrong disk (or none).
        sb.AppendLine(CultureInfo.InvariantCulture, $"select disk {diskIndex}");

        foreach (var partition in ordered)
        {
            switch (partition.PartitionType)
            {
                case PartitionType.EfiSystem:
                    sb.AppendLine(CultureInfo.InvariantCulture, $"create partition efi size={partition.SizeMb}");
                    sb.AppendLine("format fs=fat32 quick label=\"System\"");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"assign letter={espLetter}");
                    break;
                case PartitionType.Msr:
                    sb.AppendLine(CultureInfo.InvariantCulture, $"create partition msr size={partition.SizeMb}");
                    break;
                case PartitionType.Windows:
                    sb.AppendLine(CultureInfo.InvariantCulture, $"create partition primary size={windowsSizeMb}");
                    sb.AppendLine("format fs=ntfs quick label=\"Windows\"");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"assign letter={windowsLetter}");
                    break;
                case PartitionType.Recovery:
                    sb.AppendLine(CultureInfo.InvariantCulture, $"create partition primary size={partition.SizeMb}");
                    sb.AppendLine("format fs=ntfs quick label=\"Recovery\"");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"assign letter={recoveryLetter}");
                    sb.AppendLine("gpt attributes=0x8000000000000001");
                    break;
            }
        }

        return sb.ToString();
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

    /// <summary>Returns the target disk's total size in megabytes, via WMI.</summary>
    private static long GetDiskSizeMb(int diskIndex)
    {
        using var searcher = new ManagementObjectSearcher($"SELECT Size FROM Win32_DiskDrive WHERE Index = {diskIndex}");
        using var results = searcher.Get();
        foreach (ManagementObject disk in results)
        {
            if (TryConvertDiskSizeBytes(disk["Size"], out var bytes))
            {
                return (long)(bytes / (1024 * 1024));
            }
        }

        throw new InvalidOperationException($"Could not determine the size of disk {diskIndex}.");
    }

    /// <summary>
    /// Converts a <c>Win32_DiskDrive.Size</c> property value to bytes.
    ///
    /// <para>
    /// The property is a CIM <c>uint64</c>, which WMI surfaces as a boxed <see cref="ulong"/> on
    /// some providers and as a decimal string on others, so neither representation can be assumed:
    /// accepting only one of them fails formatting on every device that reports the other. The
    /// value is also null on a disk whose geometry cannot be read at all.
    /// </para>
    /// </summary>
    internal static bool TryConvertDiskSizeBytes(object? raw, out ulong bytes)
    {
        bytes = 0;
        if (raw is null)
        {
            return false;
        }

        try
        {
            bytes = Convert.ToUInt64(raw, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return false;
        }

        return bytes > 0;
    }

    /// <summary>
    /// Reserves <paramref name="count"/> distinct, currently-unused drive letters up front.
    /// They must all be chosen before diskpart runs — a single diskpart invocation creates and
    /// assigns all partitions before Windows refreshes its mounted-volume view, so
    /// <see cref="DriveInfo.GetDrives"/> would not yet reflect letters assigned earlier in the
    /// same script.
    /// </summary>
    private static string[] AllocateDriveLetters(int count)
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        var result = new List<string>();
        for (var c = 'C'; c <= 'Z' && result.Count < count; c++)
        {
            if (used.Add(c))
            {
                result.Add(c.ToString());
            }
        }

        if (result.Count < count)
        {
            throw new InvalidOperationException("Not enough available drive letters could be found.");
        }

        return [.. result];
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
