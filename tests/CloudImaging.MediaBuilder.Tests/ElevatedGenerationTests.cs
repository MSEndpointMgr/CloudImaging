using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using CloudImaging.MediaBuilder.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudImaging.MediaBuilder.Tests;

/// <summary>
/// Tests for the DISM-elevation-aware boot image generation path (FR-051).
///
/// DISM image mounting requires Administrator privileges, but the main Media Builder
/// process must stay non-elevated because Entra ID sign-in relies on MSAL's Windows
/// broker (WAM), which does not work reliably from an elevated process.
/// <see cref="BootImageGenerationService.GenerateElevatedAsync"/> reconciles this by
/// relaunching this same executable elevated (one UAC prompt) as a short-lived worker
/// when the current process isn't already elevated.
///
/// These tests inject fakes for both the elevation check and the process launcher so
/// no real UAC prompt is ever triggered during automated test runs.
/// </summary>
public sealed class ElevatedGenerationTests
{
    [Fact]
    public async Task GenerateElevatedAsync_SkipsRelaunch_WhenAlreadyElevated()
    {
        // When already elevated, GenerateElevatedAsync must behave exactly like calling
        // GenerateAsync directly (no relaunch, no process spawned).
        var launcherCalled = false;
        var svc = new BootImageGenerationService(
            NullLogger<BootImageGenerationService>.Instance,
            isElevatedOverride: () => true,
            startElevatedProcessOverride: (_, _) =>
            {
                launcherCalled = true;
                throw new InvalidOperationException("Should not be called when already elevated.");
            });

        Func<Task> act = async () => await svc.GenerateElevatedAsync(
            clientBinariesPath: @"C:\DoesNotExist",
            pfxBytes: null,
            outputDirectory: Path.GetTempPath());

        await act.Should().ThrowAsync<Exception>(
            "generation should still fail fast (invalid client path / ADK) exactly as GenerateAsync would");
        launcherCalled.Should().BeFalse("no elevated process should be spawned when already elevated");
    }

    [Fact]
    public async Task GenerateElevatedAsync_RelaunchesElevated_AndSurfacesSuccessResult()
    {
        var svc = new BootImageGenerationService(
            NullLogger<BootImageGenerationService>.Instance,
            isElevatedOverride: () => false,
            startElevatedProcessOverride: (_, args) =>
            {
                // Simulate the elevated worker process: parse the IPC file paths out of the
                // command-line and write a canned success result directly, instead of really
                // elevating and running BootImageGenerationService.RunElevatedWorkerAsync.
                var files = Regex.Matches(args, "\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToArray();
                var resultFile = files[2];
                File.WriteAllText(resultFile,
                    """{"Success":true,"WimPath":"C:\\out\\cloud-imaging-boot.wim","Sha256Hash":"deadbeef"}""");

                // Return a real, harmless, already-exiting process so HasExited/Dispose work.
                return Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0")
                {
                    UseShellExecute = false,
                    CreateNoWindow  = true,
                })!;
            });

        var result = await svc.GenerateElevatedAsync(
            clientBinariesPath: @"C:\DoesNotExist",
            pfxBytes: null,
            outputDirectory: Path.GetTempPath());

        result.WimPath.Should().Be(@"C:\out\cloud-imaging-boot.wim");
        result.Sha256Hash.Should().Be("deadbeef");
    }

    [Fact]
    public async Task GenerateElevatedAsync_SurfacesFailureResult_FromElevatedWorker()
    {
        var svc = new BootImageGenerationService(
            NullLogger<BootImageGenerationService>.Instance,
            isElevatedOverride: () => false,
            startElevatedProcessOverride: (_, args) =>
            {
                var files = Regex.Matches(args, "\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToArray();
                var resultFile = files[2];
                File.WriteAllText(resultFile,
                    """{"Success":false,"WimPath":null,"Sha256Hash":null,"Error":"dism.exe exited with code 1."}""");

                return Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0")
                {
                    UseShellExecute = false,
                    CreateNoWindow  = true,
                })!;
            });

        Func<Task> act = async () => await svc.GenerateElevatedAsync(
            clientBinariesPath: @"C:\DoesNotExist",
            pfxBytes: null,
            outputDirectory: Path.GetTempPath());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("dism.exe exited with code 1.");
    }

    [Fact]
    public async Task GenerateElevatedAsync_ThrowsClearMessage_WhenElevationCancelled()
    {
        var svc = new BootImageGenerationService(
            NullLogger<BootImageGenerationService>.Instance,
            isElevatedOverride: () => false,
            startElevatedProcessOverride: (_, _) =>
                throw new System.ComponentModel.Win32Exception(1223, "The operation was canceled by the user."));

        Func<Task> act = async () => await svc.GenerateElevatedAsync(
            clientBinariesPath: @"C:\DoesNotExist",
            pfxBytes: null,
            outputDirectory: Path.GetTempPath());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*elevation was cancelled*");
    }
}
