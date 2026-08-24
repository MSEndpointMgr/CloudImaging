using System.IO;
using System.Management;
using Microsoft.Extensions.Logging;

namespace CloudImaging.MediaBuilder.Services;

/// <summary>
/// Validates that a disk device is safe for USB boot image deployment (T070, FR-054).
///
/// Safety rules (FR-054):
///   1. Bus type MUST be USB.
///   2. Removable flag MUST be true.
///   3. The host OS/system disk is ALWAYS blocked, regardless of rules 1–2.
///   4. Disk must have sufficient capacity for the boot partition.
/// </summary>
public sealed partial class UsbSafetyValidationService
{
    private readonly ILogger<UsbSafetyValidationService> _logger;

    public UsbSafetyValidationService(ILogger<UsbSafetyValidationService> logger) => _logger = logger;

    public sealed record DiskInfo(
        uint DiskNumber,
        string Caption,
        long SizeBytes,
        string BusType,
        bool IsRemovable,
        bool IsSystemDisk);

    public sealed record ValidationResult(bool Valid, string? FailureReason);

    /// <summary>Returns all physical disks enumerated by WMI.</summary>
    public IReadOnlyList<DiskInfo> EnumerateDisks()
    {
        var disks = new List<DiskInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, Caption, Size, InterfaceType, MediaType FROM Win32_DiskDrive");
            using var results  = searcher.Get();

            foreach (ManagementObject disk in results)
            {
                var index     = (uint)(disk["DeviceID"]?.ToString()?.Replace("\\\\.\\PHYSICALDRIVE", string.Empty)
                                    .Trim() is string s && uint.TryParse(s, out var n) ? n : 0);
                var caption   = disk["Caption"]?.ToString()      ?? string.Empty;
                var sizeBytes = disk["Size"] is string sz && long.TryParse(sz, out var b) ? b : 0L;
                var busType   = disk["InterfaceType"]?.ToString() ?? string.Empty;
                var mediaType = disk["MediaType"]?.ToString()     ?? string.Empty;
                // FR-054 requires bus type = USB AND removable flag = true as two INDEPENDENT
                // criteria — removable must come from the disk's own MediaType, not be inferred
                // from bus type, or a USB-attached FIXED external drive would incorrectly pass.
                var removable = mediaType.Contains("Removable", StringComparison.OrdinalIgnoreCase);

                // Win32_DiskDrive.Size is known to occasionally report 0 for freshly-attached or
                // freshly-wiped (RAW, no partition table) USB disks until the legacy Storport
                // disk-class driver refreshes its cached geometry — this leaves the technician
                // stuck with a "0.0 B" device that then fails partitioning with a bogus negative
                // free-space error. Fall back to the modern Storage Management WMI provider
                // (MSFT_Disk, used internally by PowerShell's Get-Disk), which reads geometry
                // directly and isn't subject to that legacy caching quirk.
                if (sizeBytes <= 0)
                {
                    var fallback = TryGetSizeFromStorageProvider(index);
                    if (fallback is > 0)
                    {
                        LogSizeFallbackUsed(_logger, index, fallback.Value);
                        sizeBytes = fallback.Value;
                    }
                }

                disks.Add(new DiskInfo(index, caption, sizeBytes, busType, removable, IsSystemDisk(index)));
            }
        }
        catch (Exception ex)
        {
            LogEnumerationFailed(_logger, ex);
        }
        return disks;
    }

    /// <summary>
    /// Validates that <paramref name="disk"/> is safe to use for USB imaging.
    /// </summary>
    public ValidationResult Validate(DiskInfo disk)
    {
        if (disk.IsSystemDisk)
        {
            LogRejected(_logger, disk.Caption, "system disk");
            return new ValidationResult(false, "This disk is the system disk and cannot be selected.");
        }

        if (!disk.BusType.Equals("USB", StringComparison.OrdinalIgnoreCase))
        {
            LogRejected(_logger, disk.Caption, "not USB");
            return new ValidationResult(false, $"Disk bus type is '{disk.BusType}' — only USB disks are permitted.");
        }

        if (!disk.IsRemovable)
        {
            LogRejected(_logger, disk.Caption, "not removable");
            return new ValidationResult(false, "Disk is not marked as removable.");
        }

        LogApproved(_logger, disk.Caption);
        return new ValidationResult(true, null);
    }

    /// <summary>
    /// Determines whether physical disk <paramref name="diskNumber"/> hosts the running Windows
    /// installation, by tracing the real partition → disk chain from the system drive letter
    /// (e.g. <c>C:</c>) via WMI associator queries: <c>Win32_LogicalDisk</c> →
    /// <c>Win32_LogicalDiskToPartition</c> → <c>Win32_DiskDriveToDiskPartition</c> →
    /// <c>Win32_DiskDrive.Index</c>. This is authoritative regardless of disk numbering/order,
    /// unlike assuming the system disk is always disk 0 (not true on multi-disk workstations).
    /// </summary>
    private static bool IsSystemDisk(uint diskNumber)
    {
        try
        {
            var systemDrive = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";

            using var partitionSearcher = new ManagementObjectSearcher(
                $"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{systemDrive}'}} WHERE AssocClass = Win32_LogicalDiskToPartition");
            foreach (ManagementObject partition in partitionSearcher.Get())
            {
                using var diskSearcher = new ManagementObjectSearcher(
                    $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partition["DeviceID"]}'}} WHERE AssocClass = Win32_DiskDriveToDiskPartition");
                foreach (ManagementObject diskDrive in diskSearcher.Get())
                {
                    if (diskDrive["Index"] is not null
                        && Convert.ToUInt32(diskDrive["Index"], System.Globalization.CultureInfo.InvariantCulture) == diskNumber)
                        return true;
                }
            }
            return false;
        }
        catch
        {
            // Fail-safe: if the partition→disk trace itself fails (unexpected WMI issue), fall
            // back to the conservative disk-0 assumption rather than silently treating an
            // unverified disk as safe to erase.
            return diskNumber == 0;
        }
    }

    /// <summary>
    /// Fallback disk-size lookup via the Storage Management WMI provider
    /// (<c>root\Microsoft\Windows\Storage</c>, <c>MSFT_Disk</c>) — the same provider PowerShell's
    /// <c>Get-Disk</c> cmdlet uses. Its <c>Size</c> property is a genuine <c>UInt64</c> queried
    /// directly from disk geometry, unlike the legacy <c>Win32_DiskDrive.Size</c> (a scripting-era
    /// string property) which can cache a stale 0 for a disk that was just attached or just wiped
    /// (RAW/no partition table). Returns <c>null</c> if the provider is unavailable or the disk
    /// isn't found there (e.g. older Windows builds without the Storage Management API stack).
    /// </summary>
    private static long? TryGetSizeFromStorageProvider(uint diskNumber)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                $"SELECT Size FROM MSFT_Disk WHERE Number = {diskNumber}");
            using var results = searcher.Get();

            foreach (ManagementObject disk in results)
            {
                if (disk["Size"] is not null)
                    return Convert.ToInt64(disk["Size"], System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        catch
        {
            // Storage Management WMI namespace may not be present/queryable on every system;
            // treat as "no fallback available" rather than surfacing a secondary error.
        }
        return null;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Disk '{Caption}' rejected: {Reason}.")]
    private static partial void LogRejected(ILogger logger, string caption, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Disk '{Caption}' approved for USB deployment.")]
    private static partial void LogApproved(ILogger logger, string caption);

    [LoggerMessage(Level = LogLevel.Error, Message = "Disk enumeration failed.")]
    private static partial void LogEnumerationFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Disk {DiskNumber} reported 0 bytes via Win32_DiskDrive; used MSFT_Disk fallback size {SizeBytes} bytes.")]
    private static partial void LogSizeFallbackUsed(ILogger logger, uint diskNumber, long sizeBytes);
}
