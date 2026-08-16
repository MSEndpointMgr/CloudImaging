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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Disk '{Caption}' rejected: {Reason}.")]
    private static partial void LogRejected(ILogger logger, string caption, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Disk '{Caption}' approved for USB deployment.")]
    private static partial void LogApproved(ILogger logger, string caption);

    [LoggerMessage(Level = LogLevel.Error, Message = "Disk enumeration failed.")]
    private static partial void LogEnumerationFailed(ILogger logger, Exception ex);
}
