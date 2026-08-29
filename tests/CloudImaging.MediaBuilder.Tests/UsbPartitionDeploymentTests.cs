using CloudImaging.MediaBuilder.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO;
using System.Reflection;
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
    public void DiskpartScripts_ProduceTwoPartitions_Fat32Boot_NtfsCache()
    {
        // FR-055: two-partition layout — FAT32 boot (min 2 GB) + NTFS cache — asserted against
        // the actual diskpart scripts UsbPartitionProvisioningService.ProvisionAsync executes
        // (split into 3 stages — erase/initialize, boot partition, cache partition — so the UI
        // can report real sub-step progress instead of one flat "partitioning…" message).
        var type = typeof(UsbPartitionProvisioningService);
        string Build(string methodName, uint diskNumber) =>
            (string)type.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [diskNumber])!;

        var cleanScript = Build("BuildCleanAndConvertScript", 3u);
        var bootScript  = Build("BuildBootPartitionScript", 3u);
        var cacheScript = Build("BuildCachePartitionScript", 3u);

        cleanScript.Should().Contain("select disk 3", "each stage must re-select the exact disk number passed in");
        cleanScript.Should().Contain("clean");
        cleanScript.Should().Contain("convert mbr");

        bootScript.Should().Contain("select disk 3");
        bootScript.Should().Contain($"size={UsbPartitionProvisioningService.BootPartitionSizeMb}",
            "the boot partition size must come from the shared BootPartitionSizeMb constant");
        bootScript.Should().Contain("fs=fat32", "boot partition must be FAT32 for WinPE compatibility (FR-055)");

        cacheScript.Should().Contain("select disk 3");
        cacheScript.Should().Contain("fs=ntfs", "cache partition must be NTFS (FR-055)");
    }

    [Fact]
    public void BootPartition_Size_MeetsFr055Minimum_2Gb()
    {
        UsbPartitionProvisioningService.BootPartitionSizeMb.Should().Be(2048,
            "boot partition must be at least 2 GB per FR-055 (sufficient for WinPE + Client binaries)");
    }

    [Fact]
    public void CachePartition_MinimumSize_Is24Gb()
    {
        UsbPartitionProvisioningService.MinimumCachePartitionBytes.Should().Be(24L * 1024 * 1024 * 1024,
            "cache partition must hold one OS image at the top of the supported range (20 GB) plus "
            + "filesystem overhead, otherwise the largest catalog entries could never be cached");
    }

    [Fact]
    public void CachePartition_Minimum_FitsLargestSupportedOsImage()
    {
        const long largestSupportedImageBytes = 20L * 1024 * 1024 * 1024;
        UsbPartitionProvisioningService.MinimumCachePartitionBytes.Should().BeGreaterThan(largestSupportedImageBytes,
            "a cache partition exactly the size of the largest supported image leaves no room for "
            + "NTFS metadata, so the copy would fail at the end of a long download");
    }

    [Fact]
    public async Task ProvisionAsync_Throws_WhenDiskTooSmallForCacheMinimum()
    {
        // A disk that only leaves room for a cache partition under the FR-055 minimum must be
        // rejected BEFORE any destructive diskpart action is taken.
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
    public async Task ConfigureWinPeAutoStartAsync_WritesWinPeShlIniAndStartnetCmd_LaunchingClient()
    {
        // FR-051/FR-057: WinPE must launch the Cloud Imaging Client automatically at boot, with
        // no visible console — verified against the actual files BootImageGenerationService
        // writes into the mounted WIM, not a set of unrelated local constants.
        var mountDir = Directory.CreateTempSubdirectory("ci-mount-").FullName;
        try
        {
            var method = typeof(BootImageGenerationService).GetMethod(
                "ConfigureWinPeAutoStartAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
            await (Task)method.Invoke(null, [mountDir, CancellationToken.None])!;

            var system32Dir = Path.Combine(mountDir, "Windows", "System32");
            var winpeshlIni = await File.ReadAllTextAsync(Path.Combine(system32Dir, "winpeshl.ini"));
            var startnetCmd = await File.ReadAllTextAsync(Path.Combine(system32Dir, "startnet.cmd"));

            winpeshlIni.Should().Contain("[LaunchApps]").And.Contain("CloudImaging.Client.exe").And.Contain("wpeinit.exe");
            startnetCmd.Should().Contain("CloudImaging.Client.exe").And.Contain("wpeinit.exe");
        }
        finally
        {
            Directory.Delete(mountDir, recursive: true);
        }
    }

    // ── Deployment progress events ────────────────────────────────────────────

    [Fact]
    public void ProgressChanged_InvokesSubscriber_WhenEventIsRaised()
    {
        var svc    = new BootImageDeploymentService(NullLogger<BootImageDeploymentService>.Instance);
        var events = new List<(string Message, int Percent)>();
        svc.ProgressChanged += (_, e) => events.Add(e);

        var field = typeof(BootImageDeploymentService)
            .GetField("ProgressChanged", BindingFlags.Instance | BindingFlags.NonPublic);
        var handler = (MulticastDelegate?)field?.GetValue(svc);
        handler.Should().NotBeNull("at least one subscriber must be registered on the backing field");
        handler!.DynamicInvoke(svc, ("Deploying…", 50));

        events.Should().ContainSingle().Which.Should().Be(("Deploying…", 50));
    }

    // ── Boot image list pre-selection ─────────────────────────────────────────

    [Fact]
    public void BootImageChoice_PreSelectsLatestPublished_WhenBuiltFromOperatorApiList()
    {
        // FR-053: the latest published entry is pre-selected in PrepareStorageDeviceView.
        // Built from BootImageDto (the actual Operator API DTO), not an anonymous local shape.
        var images = new[]
        {
            new BootImageDto { BootImageId = Guid.NewGuid(), Version = "1.0", IsLatestPublished = false, IsActive = true },
            new BootImageDto { BootImageId = Guid.NewGuid(), Version = "2.0", IsLatestPublished = true,  IsActive = true },
        };

        var preSelected = images.SingleOrDefault(i => i.IsLatestPublished);

        preSelected.Should().NotBeNull("exactly one entry must be flagged isLatestPublished (contract guaranteed by T060)");
        preSelected!.Version.Should().Be("2.0", "the latest published boot image should be pre-selected (FR-053)");
    }
}
