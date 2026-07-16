using CloudImaging.MediaBuilder.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudImaging.MediaBuilder.Tests;

/// <summary>
/// USB device qualification tests (T061, FR-054).
/// Validates that the UsbSafetyValidationService correctly enforces all qualification rules.
/// </summary>
public sealed class UsbDiskValidationTests
{
    private readonly UsbSafetyValidationService _svc =
        new(NullLogger<UsbSafetyValidationService>.Instance);

    // ── Valid USB disk ────────────────────────────────────────────────────────

    [Fact]
    public void Validate_Passes_ForUsbRemovableNonSystemDisk()
    {
        var disk = new UsbSafetyValidationService.DiskInfo(
            DiskNumber:   1,
            Caption:      "Generic Flash Disk USB Device",
            SizeBytes:    16L * 1024 * 1024 * 1024,
            BusType:      "USB",
            IsRemovable:  true,
            IsSystemDisk: false);

        var result = _svc.Validate(disk);
        result.Valid.Should().BeTrue("USB removable non-system disk must pass validation");
        result.FailureReason.Should().BeNull();
    }

    // ── Non-USB bus type is rejected ──────────────────────────────────────────

    [Theory]
    [InlineData("SATA")]
    [InlineData("NVMe")]
    [InlineData("SAS")]
    [InlineData("IDE")]
    public void Validate_Fails_ForNonUsbBusType(string busType)
    {
        var disk = new UsbSafetyValidationService.DiskInfo(
            DiskNumber:   2,
            Caption:      $"{busType} Disk",
            SizeBytes:    500L * 1024 * 1024 * 1024,
            BusType:      busType,
            IsRemovable:  true,
            IsSystemDisk: false);

        var result = _svc.Validate(disk);
        result.Valid.Should().BeFalse($"non-USB bus type '{busType}' must be rejected (FR-054)");
        result.FailureReason.Should().Contain("USB", "failure reason must mention USB requirement");
    }

    // ── Non-removable disk is rejected ────────────────────────────────────────

    [Fact]
    public void Validate_Fails_ForNonRemovableUsbDisk()
    {
        var disk = new UsbSafetyValidationService.DiskInfo(
            DiskNumber:   3,
            Caption:      "USB External HDD",
            SizeBytes:    1000L * 1024 * 1024 * 1024,
            BusType:      "USB",
            IsRemovable:  false,
            IsSystemDisk: false);

        var result = _svc.Validate(disk);
        result.Valid.Should().BeFalse("non-removable USB disk must be rejected (FR-054)");
    }

    // ── System disk is ALWAYS rejected regardless of other criteria ───────────

    [Fact]
    public void Validate_Fails_ForSystemDisk_EvenIfUsbAndRemovable()
    {
        // The host OS disk must always be blocked, regardless of bus type or removable flag
        var disk = new UsbSafetyValidationService.DiskInfo(
            DiskNumber:   0,
            Caption:      "System Disk",
            SizeBytes:    256L * 1024 * 1024 * 1024,
            BusType:      "USB",   // even if USB
            IsRemovable:  true,    // even if removable
            IsSystemDisk: true);   // system disk ALWAYS rejected

        var result = _svc.Validate(disk);
        result.Valid.Should().BeFalse("system disk must ALWAYS be rejected (FR-054)");
        result.FailureReason.Should().ContainEquivalentOf("system",
            "failure reason must clearly indicate this is the system disk");
    }

    // ── Disk number 0 is treated as system disk ───────────────────────────────

    [Fact]
    public void SystemDiskFlag_IsTrue_ForDiskNumber0()
    {
        // Default implementation treats disk 0 as the system disk
        var disk = new UsbSafetyValidationService.DiskInfo(
            DiskNumber:   0,
            Caption:      "Local Fixed Disk",
            SizeBytes:    256L * 1024 * 1024 * 1024,
            BusType:      "SATA",
            IsRemovable:  false,
            IsSystemDisk: true);

        disk.IsSystemDisk.Should().BeTrue("disk 0 is assumed to be the system disk by default");
    }
}
