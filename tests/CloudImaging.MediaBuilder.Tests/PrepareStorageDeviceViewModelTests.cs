using CloudImaging.MediaBuilder.Services;
using CloudImaging.MediaBuilder.ViewModels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO;
using System.Net.Http;
using Xunit;

namespace CloudImaging.MediaBuilder.Tests;

/// <summary>
/// PrepareStorageDeviceView gating tests (FR-054, FR-055).
///
/// The destructive "Prepare USB Device" action must be blocked unless a valid disk and a
/// boot image are selected AND the technician has explicitly confirmed the data-erase.
/// </summary>
public sealed class PrepareStorageDeviceViewModelTests
{
    [Fact]
    public void CanPrepare_IsFalse_WhenNothingSelected()
    {
        var vm = CreateViewModel();
        vm.CanPrepare.Should().BeFalse("no disk, boot image, or confirmation has been provided");
    }

    [Fact]
    public void CanPrepare_IsFalse_WhenConfirmationMissing()
    {
        var vm = CreateViewModel();
        vm.SelectedBootImage = CreateBootImage();
        vm.SelectedDisk      = CreateValidDisk();
        vm.ConfirmErase      = false;

        vm.CanPrepare.Should().BeFalse("the destructive action requires explicit confirmation (FR-055)");
    }

    [Fact]
    public void CanPrepare_IsFalse_WhenSelectedDiskIsIneligible()
    {
        var vm = CreateViewModel();
        vm.SelectedBootImage = CreateBootImage();
        vm.SelectedDisk      = CreateInvalidDisk();
        vm.ConfirmErase      = true;

        vm.CanPrepare.Should().BeFalse("system / non-removable disks must never be selectable targets (FR-054)");
    }

    [Fact]
    public void CanPrepare_IsFalse_WhenBootImageMissing()
    {
        var vm = CreateViewModel();
        vm.SelectedDisk = CreateValidDisk();
        vm.ConfirmErase = true;

        vm.CanPrepare.Should().BeFalse("a boot image must be selected before preparing media");
    }

    [Fact]
    public void CanPrepare_IsTrue_WhenAllRequirementsMet()
    {
        var vm = CreateViewModel();
        vm.SelectedBootImage = CreateBootImage();
        vm.SelectedDisk      = CreateValidDisk();
        vm.ConfirmErase      = true;

        vm.CanPrepare.Should().BeTrue("a valid disk, a boot image, and confirmation satisfy all gates");
    }

    [Fact]
    public void ValidationMessage_SurfacesReason_ForIneligibleDisk()
    {
        var vm = CreateViewModel();
        vm.SelectedDisk = CreateInvalidDisk();

        vm.HasValidationMessage.Should().BeTrue();
        vm.ValidationMessage.Should().Be("System disk cannot be used.");
    }

    // ── Support reference codes on failure (T160, FR-058) ─────────────────────

    [Fact]
    public async Task PrepareAsync_SurfacesSupportReferenceCode_OnFailure()
    {
        // No Entra sign-in is performed in this test, so GetAccessTokenAsync returns null
        // and the workflow fails right after disk (re-)validation — i.e. during the DVI stage.
        var vm = CreateViewModel();
        vm.SelectedBootImage = CreateBootImage();
        vm.SelectedDisk      = CreateValidDisk();
        vm.ConfirmErase      = true;

        vm.PrepareCommand.Execute(null);
        await WaitUntilIdleAsync(vm);

        vm.HasError.Should().BeTrue("preparation must fail without a signed-in Operator API session");
        vm.ErrorMessage.Should().MatchRegex(@"CMB-PREPUSB-DVI-\d+",
            "every Media Builder failure path must surface a support reference code (T160, FR-058)");
    }

    private static async Task WaitUntilIdleAsync(PrepareStorageDeviceViewModel vm)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (vm.IsBusy && DateTime.UtcNow < deadline)
            await Task.Delay(10);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static PrepareStorageDeviceViewModel.BootImageChoice CreateBootImage() =>
        new(Guid.NewGuid(), "v1.0.0", new BootImageDto
        {
            BootImageId       = Guid.NewGuid(),
            Version           = "1.0.0",
            SizeBytes         = 500_000_000,
            Sha256Hash        = "abc",
            IsLatestPublished = true,
            IsActive          = true,
        });

    private static PrepareStorageDeviceViewModel.DiskChoice CreateValidDisk() =>
        new(1, "Disk 1: SanDisk USB", new UsbSafetyValidationService.DiskInfo(
            1, "SanDisk USB", 16_000_000_000, "USB", true, false), true, null);

    private static PrepareStorageDeviceViewModel.DiskChoice CreateInvalidDisk() =>
        new(0, "Disk 0: System", new UsbSafetyValidationService.DiskInfo(
            0, "System", 512_000_000_000, "NVMe", false, true), false, "System disk cannot be used.");

    private static PrepareStorageDeviceViewModel CreateViewModel()
    {
        var http           = new HttpClient();
        var operatorApi    = new OperatorApiClient(http, NullLogger<OperatorApiClient>.Instance);
        var authService    = new EntraAuthenticationService(
            "test-client", "test-tenant", "api://test-client/.default",
            NullLogger<EntraAuthenticationService>.Instance);
        var validator      = new UsbSafetyValidationService(NullLogger<UsbSafetyValidationService>.Instance);
        var downloader     = new BootImageDownloadService(http, NullLogger<BootImageDownloadService>.Instance);
        var provisioner    = new UsbPartitionProvisioningService(NullLogger<UsbPartitionProvisioningService>.Instance);
        var deployer       = new BootImageDeploymentService(NullLogger<BootImageDeploymentService>.Instance);
        var cache          = new BootImageCacheService(
            Path.Combine(Path.GetTempPath(), $"ci-cache-tests-{Guid.NewGuid():N}"),
            NullLogger<BootImageCacheService>.Instance);

        return new PrepareStorageDeviceViewModel(
            operatorApi, authService, validator, downloader, provisioner, deployer, cache,
            navigateBack: () => { });
    }
}
