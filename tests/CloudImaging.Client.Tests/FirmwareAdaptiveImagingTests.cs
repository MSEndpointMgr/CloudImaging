using CloudImaging.Client.Services;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using FluentAssertions;
using Xunit;

namespace CloudImaging.Client.Tests;

/// <summary>
/// The disk layout and boot files follow the firmware mode: GPT + UEFI boot files on UEFI,
/// MBR + BIOS boot files on Legacy BIOS (CSM), both derived from the same partitioning scheme.
/// </summary>
public sealed class FirmwareAdaptiveImagingTests
{
    private static string[] ScriptLines(FirmwareMode mode)
    {
        var ordered = DiskFormatService.ValidateAndOrder(PartitioningScheme.Default);
        return DiskFormatService.BuildDiskpartScript(0, ordered, mode, 100_000, "S", "W", "R")
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public void Uefi_BuildsGptLayoutWithEspAndMsr()
    {
        var lines = ScriptLines(FirmwareMode.Uefi);

        lines.Should().Contain("convert gpt");
        lines.Should().Contain("create partition efi size=500");
        lines.Should().Contain("format fs=fat32 quick label=\"System\"");
        lines.Should().Contain("create partition msr size=16");
        lines.Should().Contain("gpt attributes=0x8000000000000001");
        lines.Should().NotContain("convert mbr");
        lines.Should().NotContain("active");
        lines.Should().NotContain("set id=27");
    }

    [Fact]
    public void LegacyBios_BuildsMbrLayoutWithActiveNtfsSystemPartition()
    {
        var lines = ScriptLines(FirmwareMode.LegacyBios);

        lines.Should().Contain("convert mbr");
        lines.Should().NotContain("convert gpt");
        lines.Should().NotContain(l => l.StartsWith("create partition efi", StringComparison.Ordinal));
        lines.Should().NotContain(l => l.StartsWith("create partition msr", StringComparison.Ordinal), "MBR has no MSR");
        lines.Should().NotContain(l => l.StartsWith("gpt attributes", StringComparison.Ordinal));

        var systemIndex = Array.IndexOf(lines, "create partition primary size=500");
        systemIndex.Should().BeGreaterThan(0);
        lines[systemIndex + 1].Should().Be("format fs=ntfs quick label=\"System\"");
        lines[systemIndex + 2].Should().Be("active");
        lines[systemIndex + 3].Should().Be("assign letter=S");

        lines.Should().Contain("create partition primary size=100000");
        lines[^1].Should().Be("set id=27", "the Recovery partition is typed 0x27 on MBR");
    }

    [Fact]
    public void LegacyBios_ReselectsDiskAfterRescan()
    {
        var lines = ScriptLines(FirmwareMode.LegacyBios);
        lines.Take(5).Should().Equal("select disk 0", "clean", "convert mbr", "rescan", "select disk 0");
    }

    [Fact]
    public void ReservedSize_ExcludesMsrOnlyOnMbr()
    {
        var ordered = DiskFormatService.ValidateAndOrder(PartitioningScheme.Default);

        DiskFormatService.ReservedSizeMb(ordered, FirmwareMode.Uefi).Should().Be(500 + 16 + 990);
        DiskFormatService.ReservedSizeMb(ordered, FirmwareMode.LegacyBios).Should().Be(500 + 990);
    }

    [Fact]
    public void MbrCap_IsTwoTebibytes()
    {
        DiskFormatService.MbrMaxDiskMb.Should().Be(2L * 1024 * 1024);
    }

    [Theory]
    [InlineData(FirmwareMode.Uefi, "\"W:\\Windows\" /s S: /f UEFI")]
    [InlineData(FirmwareMode.LegacyBios, "\"W:\\Windows\" /s S: /f BIOS")]
    public void Bcdboot_TargetsTheFirmwareMode(FirmwareMode mode, string expected)
    {
        BootConfigurationService.BuildBcdbootArguments("W:", "S:", mode).Should().Be(expected);
    }

    [Theory]
    [InlineData(1u, FirmwareMode.LegacyBios)]
    [InlineData(2u, FirmwareMode.Uefi)]
    [InlineData(0u, FirmwareMode.Unknown)]
    [InlineData(3u, FirmwareMode.Unknown)]
    public void FirmwareType_Maps(uint raw, FirmwareMode expected)
    {
        DevicePostureDetector.MapFirmwareType(raw).Should().Be(expected);
    }

    [Theory]
    [InlineData((byte)1, 0, SecureBootState.Enabled)]
    [InlineData((byte)0, 0, SecureBootState.Disabled)]
    [InlineData(null, 203, SecureBootState.Unsupported)]
    [InlineData(null, 1, SecureBootState.Unsupported)]
    [InlineData(null, 5, SecureBootState.Unknown)]
    public void SecureBoot_Maps(byte? value, int lastError, SecureBootState expected)
    {
        DevicePostureDetector.MapSecureBoot(value, lastError).Should().Be(expected);
    }

    [Fact]
    public void Tpm_MapsFromAcpiTables()
    {
        DevicePostureDetector.MapTpm(null).Should().Be(TpmPresence.Unknown);
        DevicePostureDetector.MapTpm(["FACP", "TPM2"]).Should().Be(TpmPresence.Tpm20);
        DevicePostureDetector.MapTpm(["FACP", "TCPA"]).Should().Be(TpmPresence.Tpm12);
        DevicePostureDetector.MapTpm(["FACP", "APIC"]).Should().Be(TpmPresence.NotDetected);
    }
}
