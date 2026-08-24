using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using CloudImaging.MediaBuilder.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudImaging.MediaBuilder.Tests;

/// <summary>
/// Groups test classes that create real <c>%TEMP%\ci-usbprep-elevated-*</c> IPC directories via
/// <see cref="UsbPreparationService.PrepareElevatedAsync"/>'s relaunch path. That path's own
/// orphan sweep unconditionally deletes ALL such directories on every call (by design — mirrors
/// <c>BootImageGenerationService.CleanupOrphanedIpcDirs</c>), so tests in this collection must
/// run sequentially rather than in xUnit's default cross-class parallelism.
/// </summary>
[CollectionDefinition(Name)]
public sealed class UsbPrepElevatedIpcDirTestGroup
{
    public const string Name = "USB preparation elevated IPC directory";
}

[Collection(UsbPrepElevatedIpcDirTestGroup.Name)]
/// <summary>
/// Tests for the diskpart/bootsect-elevation-aware USB preparation path (FR-055, FR-057).
///
/// diskpart.exe (partitioning) and bootsect.exe (boot-partition activation) both require
/// Administrator privileges, but the main Media Builder process must stay non-elevated because
/// Entra ID sign-in relies on MSAL's Windows broker (WAM), which does not work reliably from an
/// elevated process. <see cref="UsbPreparationService.PrepareElevatedAsync"/> reconciles this by
/// relaunching this same executable elevated (one UAC prompt) as a short-lived worker when the
/// current process isn't already elevated — mirrors
/// <see cref="BootImageGenerationService.GenerateElevatedAsync"/> for DISM image mounting.
///
/// These tests inject fakes for both the elevation check and the process launcher so no real
/// UAC prompt (or diskpart.exe/bootsect.exe invocation) is ever triggered during automated runs.
/// </summary>
public sealed class ElevatedUsbPreparationTests
{
    private static UsbPreparationService.PreparationParams CreateParams() => new(
        DiskNumber: 1,
        // Deliberately below the FR-055 minimum cache partition size, so PrepareAsync's
        // "already elevated" path fails fast (in UsbPartitionProvisioningService.ProvisionAsync's
        // pre-flight size check) BEFORE ever invoking the real diskpart.exe.
        DiskSizeBytes: 8L * 1024 * 1024 * 1024,
        WimPath: @"C:\DoesNotExist\boot.wim",
        BootImageVersion: "1.0.0",
        SelectedDiskId: "1",
        BusType: "USB",
        DiskValidated: true,
        ToolVersion: "1.0.0.0");

    private static UsbPreparationService CreateService(
        Func<bool>? isElevatedOverride = null,
        Func<string, string, Process>? startElevatedProcessOverride = null) => new(
            NullLogger<UsbPreparationService>.Instance,
            new UsbPartitionProvisioningService(NullLogger<UsbPartitionProvisioningService>.Instance),
            new BootImageDeploymentService(NullLogger<BootImageDeploymentService>.Instance),
            isElevatedOverride,
            startElevatedProcessOverride);

    [Fact]
    public async Task PrepareElevatedAsync_SkipsRelaunch_WhenAlreadyElevated()
    {
        var launcherCalled = false;
        var svc = CreateService(
            isElevatedOverride: () => true,
            startElevatedProcessOverride: (_, _) =>
            {
                launcherCalled = true;
                throw new InvalidOperationException("Should not be called when already elevated.");
            });

        Func<Task> act = async () => await svc.PrepareElevatedAsync(CreateParams());

        await act.Should().ThrowAsync<InvalidOperationException>(
                "preparation should still fail fast (disk too small for FR-055 cache minimum) exactly as PrepareAsync would")
            .WithMessage("*too small*");
        launcherCalled.Should().BeFalse("no elevated process should be spawned when already elevated");
    }

    [Fact]
    public async Task PrepareElevatedAsync_RelaunchesElevated_AndSurfacesSuccessResult()
    {
        var svc = CreateService(
            isElevatedOverride: () => false,
            startElevatedProcessOverride: (_, args) =>
            {
                // Simulate the elevated worker process: parse the IPC file paths out of the
                // command-line and write a canned success result directly, instead of really
                // elevating and running UsbPreparationService.RunElevatedWorkerAsync.
                var files = Regex.Matches(args, "\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToArray();
                var resultFile = files[2];
                File.WriteAllText(resultFile,
                    """{"Success":true,"BootDriveLetter":"E:","CacheDriveLetter":"F:"}""");

                return Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0")
                {
                    UseShellExecute = false,
                    CreateNoWindow  = true,
                })!;
            });

        var result = await svc.PrepareElevatedAsync(CreateParams());

        result.BootDriveLetter.Should().Be("E:");
        result.CacheDriveLetter.Should().Be("F:");
    }

    [Fact]
    public async Task PrepareElevatedAsync_SurfacesFailureResult_FromElevatedWorker()
    {
        var svc = CreateService(
            isElevatedOverride: () => false,
            startElevatedProcessOverride: (_, args) =>
            {
                var files = Regex.Matches(args, "\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToArray();
                var resultFile = files[2];
                File.WriteAllText(resultFile,
                    """{"Success":false,"BootDriveLetter":null,"CacheDriveLetter":null,"Error":"diskpart exited with code 1."}""");

                return Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0")
                {
                    UseShellExecute = false,
                    CreateNoWindow  = true,
                })!;
            });

        Func<Task> act = async () => await svc.PrepareElevatedAsync(CreateParams());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("diskpart exited with code 1.");
    }

    [Fact]
    public async Task PrepareElevatedAsync_ThrowsClearMessage_WhenElevationCancelled()
    {
        var svc = CreateService(
            isElevatedOverride: () => false,
            startElevatedProcessOverride: (_, _) =>
                throw new System.ComponentModel.Win32Exception(1223, "The operation was canceled by the user."));

        Func<Task> act = async () => await svc.PrepareElevatedAsync(CreateParams());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*elevation was cancelled*");
    }
}
