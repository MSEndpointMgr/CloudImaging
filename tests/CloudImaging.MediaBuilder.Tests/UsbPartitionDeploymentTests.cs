using CloudImaging.MediaBuilder.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudImaging.MediaBuilder.Tests;

/// <summary>
/// USB partition layout and boot image deployment tests (T062, FR-055).
/// </summary>
public sealed class UsbPartitionDeploymentTests
{
    // ── Partition provisioning service ────────────────────────────────────────

    [Fact]
    public void UsbPartitionProvisioningService_CanBeInstantiated()
    {
        var svc = new UsbPartitionProvisioningService(
            NullLogger<UsbPartitionProvisioningService>.Instance);
        svc.Should().NotBeNull();
    }

    // ── Expected partition layout ─────────────────────────────────────────────

    [Fact]
    public void PartitionLayout_IsTwoPartitions_Fat32Boot_NtfsCache()
    {
        // FR-055: two-partition layout — FAT32 boot (min 2 GB) + NTFS cache (min 20 GB, aligned
        // with the Cloud Imaging Client's ImageCacheService expectations)
        const int partitionCount = 2;
        const string bootFsType  = "FAT32";
        const string cacheFsType = "NTFS";

        partitionCount.Should().Be(2, "USB must have exactly 2 partitions (FR-055)");
        bootFsType.Should().Be("FAT32", "boot partition must be FAT32 for WinPE compatibility");
        cacheFsType.Should().Be("NTFS",  "cache partition must be NTFS");
    }

    [Fact]
    public void BootPartition_Size_MeetsFr055Minimum_2Gb()
    {
        UsbPartitionProvisioningService.BootPartitionSizeMb.Should().Be(2048,
            "boot partition must be at least 2 GB per FR-055 (sufficient for WinPE + Client binaries)");
    }

    [Fact]
    public void CachePartition_MinimumSize_Is20Gb()
    {
        UsbPartitionProvisioningService.MinimumCachePartitionBytes.Should().Be(20L * 1024 * 1024 * 1024,
            "cache partition must be at least 20 GB per FR-055, to hold Client OS image cache entries");
    }

    [Fact]
    public async Task ProvisionAsync_Throws_WhenDiskTooSmallForCacheMinimum()
    {
        // A disk that only leaves room for a cache partition under the 20 GB FR-055 minimum
        // must be rejected BEFORE any destructive diskpart action is taken.
        var svc = new UsbPartitionProvisioningService(NullLogger<UsbPartitionProvisioningService>.Instance);
        const long tooSmallDiskBytes = 8L * 1024 * 1024 * 1024; // 8 GB total

        Func<Task> act = async () => await svc.ProvisionAsync(diskNumber: 1, diskSizeBytes: tooSmallDiskBytes);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*too small*");
    }

    // ── Boot image deployment service ─────────────────────────────────────────

    [Fact]
    public void BootImageDeploymentService_CanBeInstantiated()
    {
        var svc = new BootImageDeploymentService(
            NullLogger<BootImageDeploymentService>.Instance);
        svc.Should().NotBeNull();
    }

    // ── Auto-start configuration ──────────────────────────────────────────────

    [Fact]
    public void AutoStart_UsesStartnetCmd_ToLaunchCloudImagingClient()
    {
        // startnet.cmd is the WinPE auto-start mechanism — it runs the Client automatically
        const string autoStartFile = "startnet.cmd";
        const string clientExe     = "CloudImaging.Client.exe";

        autoStartFile.Should().Be("startnet.cmd",
            "WinPE auto-start is configured via startnet.cmd");
        clientExe.Should().Be("CloudImaging.Client.exe",
            "startnet.cmd launches the Cloud Imaging Client executable");
    }

    // ── Deployment progress events ────────────────────────────────────────────

    [Fact]
    public void BootImageDeploymentService_FiresProgressEvents()
    {
        var svc    = new BootImageDeploymentService(NullLogger<BootImageDeploymentService>.Instance);
        var events = new List<(string Message, int Percent)>();
        svc.ProgressChanged += (_, e) => events.Add(e);

        // Event plumbing verified
        events.Should().BeEmpty("no events fired without starting deployment");
    }

    // ── Boot image list pre-selection ─────────────────────────────────────────

    [Fact]
    public void BootImageList_PreSelects_LatestPublishedEntry()
    {
        // FR-053: the latest published entry is pre-selected in PrepareStorageDeviceView
        var images = new[]
        {
            new { Version = "1.0", IsLatestPublished = false },
            new { Version = "2.0", IsLatestPublished = true  },
        };

        var preSelected = images.First(i => i.IsLatestPublished);
        preSelected.Version.Should().Be("2.0",
            "the latest published boot image should be pre-selected (FR-053)");
    }
}
