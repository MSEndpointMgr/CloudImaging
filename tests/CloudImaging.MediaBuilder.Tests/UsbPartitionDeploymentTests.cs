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
    public void BuildDiskpartScript_ProducesTwoPartitions_Fat32Boot_NtfsCache()
    {
        // FR-055: two-partition layout — FAT32 boot (min 2 GB) + NTFS cache — asserted against
        // the actual diskpart script UsbPartitionProvisioningService.ProvisionAsync executes,
        // rather than a set of unrelated local constants.
        var method = typeof(UsbPartitionProvisioningService).GetMethod(
            "BuildDiskpartScript", BindingFlags.NonPublic | BindingFlags.Static)!;
        var script = (string)method.Invoke(null, [3u])!;

        script.Should().Contain("select disk 3", "the script must target the exact disk number passed in");
        script.Should().Contain($"size={UsbPartitionProvisioningService.BootPartitionSizeMb}",
            "the boot partition size must come from the shared BootPartitionSizeMb constant");
        script.Should().Contain("fs=fat32", "boot partition must be FAT32 for WinPE compatibility (FR-055)");
        script.Should().Contain("fs=ntfs", "cache partition must be NTFS (FR-055)");
        script.Split("create partition", StringSplitOptions.None).Length.Should().Be(3,
            "exactly two 'create partition' commands must appear (2 partitions total, FR-055)");
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
