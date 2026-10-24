using CloudImaging.Client.Services;
using CloudImaging.Contracts.Models;
using FluentAssertions;
using Xunit;

namespace CloudImaging.Client.Tests;

/// <summary>What the title bar's info panel shows for each way a device can be started.</summary>
public sealed class BootImageDetailsTests
{
    private static readonly UsbPreparationManifest Media = new()
    {
        ManifestVersion = "1.0",
        ToolVersion = "1",
        BootImageVersion = "2026.10.22.1",
        SelectedDiskId = "1",
        LocationName = "Stockholm",
        BootImageBase = BootImageBase.WindowsPE,
    };

    private static readonly BootImageManifest Embedded = new()
    {
        ManifestVersion = BootImageManifest.ManifestSchemaVersion,
        ImageVersion = "20261022-143012",
        CreatedAt = new DateTimeOffset(2026, 10, 22, 14, 30, 12, TimeSpan.Zero),
        Base = BootImageBase.WindowsRE,
    };

    [Fact]
    public void Rows_DescribeAPreparedUsbDrive()
    {
        var details = BootImageDetails.From("1.4.2", Media, BootMediaKind.UsbDrive, Embedded, supportsWifi: false, MachineArchitecture.X64);

        details.Rows.Select(r => (r.Label, r.Value)).Should().Equal(
            ("Client", "1.4.2"),
            ("Boot image", "2026.10.22.1"),
            ("Built", "2026-10-22 14:30 UTC"),
            ("Type", "Windows RE, x64"),
            ("Started from", "USB drive"),
            ("Location", "Stockholm"));
    }

    [Fact]
    public void Rows_FallBack_WhenNoManifestIsFound()
    {
        var details = BootImageDetails.From(null, null, BootMediaKind.UsbDrive, null, supportsWifi: true, MachineArchitecture.Arm64);

        details.Rows.Select(r => (r.Label, r.Value)).Should().Equal(
            ("Client", "Unknown"),
            ("Boot image", "Not recorded"),
            ("Built", "Unknown"),
            ("Type", "Windows RE, ARM64"),
            ("Started from", "Other media"),
            ("Location", "None"));
    }
}
