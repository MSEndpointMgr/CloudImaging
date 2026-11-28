using System.IO;
using CloudImaging.MediaBuilder.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudImaging.MediaBuilder.Tests;

/// <summary>
/// Groups test classes that create real <c>%TEMP%\ci-elevated-*</c> IPC directories via
/// <see cref="BootImageGenerationService.RunElevatedChildProcessAsync"/>. That method's
/// <c>CleanupOrphanedIpcDirs</c> step unconditionally sweeps ALL such directories on every call
/// (by design — see its doc comment), so tests in this collection must run sequentially rather
/// than in xUnit's default cross-class parallelism, or one test's in-progress IPC directory can
/// be deleted by another concurrently-running test.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ElevatedIpcDirTestGroup
{
    public const string Name = "Elevated IPC directory";
}

[Collection(ElevatedIpcDirTestGroup.Name)]
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
            FakeElevatedWorker.Inputs(),
            pfxBytes: null,
            outputDirectory: Path.GetTempPath());

        await act.Should().ThrowAsync<Exception>(
            "generation should still fail fast (invalid client path / ADK) exactly as GenerateAsync would");
        launcherCalled.Should().BeFalse("no elevated process should be spawned when already elevated");
    }

    [Fact]
    public async Task GenerateElevatedAsync_RelaunchesElevated_AndSurfacesSuccessResult()
    {
        var worker = new FakeElevatedWorker();
        var svc = new BootImageGenerationService(
            NullLogger<BootImageGenerationService>.Instance,
            isElevatedOverride: () => false,
            startElevatedProcessOverride: worker.Start);

        var result = await svc.GenerateElevatedAsync(
            FakeElevatedWorker.Inputs(),
            pfxBytes: null,
            outputDirectory: Path.GetTempPath());

        result.WimPath.Should().Be(@"C:\out\cloud-imaging-boot.wim");
        result.Sha256Hash.Should().Be("deadbeef");
    }

    [Fact]
    public async Task GenerateElevatedAsync_PromptsForElevation_BeforePreparingInputs()
    {
        var order = new List<string>();
        var worker = new FakeElevatedWorker();
        var svc = new BootImageGenerationService(
            NullLogger<BootImageGenerationService>.Instance,
            isElevatedOverride: () => false,
            startElevatedProcessOverride: (exe, args) =>
            {
                order.Add("elevate");
                return worker.Start(exe, args);
            });

        await svc.GenerateElevatedAsync(
            _ =>
            {
                order.Add("prepare");
                return Task.FromResult(new BootImageGenerationService.GenerationInputs(@"C:\DoesNotExist"));
            },
            pfxBytes: null,
            outputDirectory: Path.GetTempPath());

        order.Should().Equal(["elevate", "prepare"], "the UAC prompt comes before any download");
        worker.ReceivedParams.Should().BeTrue();
    }

    [Fact]
    public async Task GenerateElevatedAsync_StopsTheWaitingWorker_WhenPreparationFails()
    {
        var worker = new FakeElevatedWorker();
        var svc = new BootImageGenerationService(
            NullLogger<BootImageGenerationService>.Instance,
            isElevatedOverride: () => false,
            startElevatedProcessOverride: worker.Start);

        Func<Task> act = async () => await svc.GenerateElevatedAsync(
            _ => throw new InvalidOperationException("Could not download the Cloud Imaging Client."),
            pfxBytes: null,
            outputDirectory: Path.GetTempPath());

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Could not download the Cloud Imaging Client.");
        worker.WasStopped.Should().BeTrue("a worker that never gets its parameters must not keep waiting");
        worker.ReceivedParams.Should().BeFalse();
    }

    [Fact]
    public async Task GenerateElevatedAsync_StopsTheWaitingWorker_WhenCancelledDuringPreparation()
    {
        using var cts = new CancellationTokenSource();
        var worker = new FakeElevatedWorker();
        var svc = new BootImageGenerationService(
            NullLogger<BootImageGenerationService>.Instance,
            isElevatedOverride: () => false,
            startElevatedProcessOverride: worker.Start);

        Func<Task> act = async () => await svc.GenerateElevatedAsync(
            async ct =>
            {
                await cts.CancelAsync();
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException("Unreachable.");
            },
            pfxBytes: null,
            outputDirectory: Path.GetTempPath(),
            ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        worker.WasStopped.Should().BeTrue();
        worker.ReceivedParams.Should().BeFalse();
    }

    [Fact]
    public async Task RunElevatedWorkerAsync_WaitsForItsParameters_AndExitsCancelledWhenStoppedFirst()
    {
        var ipcDir = Directory.CreateTempSubdirectory("ci-worker-test-").FullName;
        try
        {
            var resultFile = Path.Combine(ipcDir, "result.json");
            var cancelFile = Path.Combine(ipcDir, "cancel.flag");
            var worker = BootImageGenerationService.RunElevatedWorkerAsync(Path.Combine(ipcDir, "params.json"), Path.Combine(ipcDir, "progress.txt"), resultFile, cancelFile, NullLoggerFactory.Instance);

            await Task.Delay(700);
            File.Exists(resultFile).Should().BeFalse("the worker waits for the parent's parameters instead of failing");

            File.WriteAllText(cancelFile, string.Empty);
            await worker.WaitAsync(TimeSpan.FromSeconds(10));

            File.ReadAllText(resultFile).Should().Contain("\"Success\":false").And.Contain("cancelled");
        }
        finally
        {
            Directory.Delete(ipcDir, recursive: true);
        }
    }

    [Fact]
    public async Task GenerateElevatedAsync_SurfacesFailureResult_FromElevatedWorker()
    {
        var worker = new FakeElevatedWorker("""{"Success":false,"WimPath":null,"Sha256Hash":null,"Error":"dism.exe exited with code 1."}""");
        var svc = new BootImageGenerationService(
            NullLogger<BootImageGenerationService>.Instance,
            isElevatedOverride: () => false,
            startElevatedProcessOverride: worker.Start);

        Func<Task> act = async () => await svc.GenerateElevatedAsync(
            FakeElevatedWorker.Inputs(),
            pfxBytes: null,
            outputDirectory: Path.GetTempPath());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("dism.exe exited with code 1.");
    }

    [Fact]
    public async Task GenerateElevatedAsync_ThrowsClearMessage_WhenElevationCancelled()
    {
        var prepared = false;
        var svc = new BootImageGenerationService(
            NullLogger<BootImageGenerationService>.Instance,
            isElevatedOverride: () => false,
            startElevatedProcessOverride: (_, _) =>
                throw new System.ComponentModel.Win32Exception(1223, "The operation was canceled by the user."));

        Func<Task> act = async () => await svc.GenerateElevatedAsync(
            _ =>
            {
                prepared = true;
                return Task.FromResult(new BootImageGenerationService.GenerationInputs(@"C:\DoesNotExist"));
            },
            pfxBytes: null,
            outputDirectory: Path.GetTempPath());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*elevation was cancelled*");
        prepared.Should().BeFalse("nothing is downloaded when the UAC prompt is declined");
    }

    [Fact]
    public async Task GenerateElevatedAsync_ThreadsEnableCommandPromptAccess_IntoElevatedWorkerParams()
    {
        // FR-051d: the "Enable command prompt access" opt-in must reach the elevated worker via
        // the same params.json IPC file used for driverRootPath/pfx/logo/deviceGatewayBaseUrl.
        string? capturedParamsJson = null;
        var worker = new FakeElevatedWorker(onParams: json => capturedParamsJson = json);
        var svc = new BootImageGenerationService(
            NullLogger<BootImageGenerationService>.Instance,
            isElevatedOverride: () => false,
            startElevatedProcessOverride: worker.Start);

        await svc.GenerateElevatedAsync(
            FakeElevatedWorker.Inputs(),
            pfxBytes: null,
            outputDirectory: Path.GetTempPath(),
            enableCommandPromptAccess: true);

        capturedParamsJson.Should().NotBeNull();
        capturedParamsJson.Should().Contain("\"EnableCommandPromptAccess\":true");
    }

    [Fact]
    public async Task GenerateElevatedAsync_ThreadsToolsRootPath_IntoElevatedWorkerParams()
    {
        // FR-051d: the support-tools folder path must reach the elevated worker via the same
        // params.json IPC file used for driverRootPath/pfx/logo/deviceGatewayBaseUrl.
        string? capturedParamsJson = null;
        var worker = new FakeElevatedWorker(onParams: json => capturedParamsJson = json);
        var svc = new BootImageGenerationService(
            NullLogger<BootImageGenerationService>.Instance,
            isElevatedOverride: () => false,
            startElevatedProcessOverride: worker.Start);

        await svc.GenerateElevatedAsync(
            FakeElevatedWorker.Inputs(),
            pfxBytes: null,
            outputDirectory: Path.GetTempPath(),
            toolsRootPath: @"C:\tools");

        capturedParamsJson.Should().NotBeNull();
        capturedParamsJson.Should().Contain(@"""ToolsRootPath"":""C:\\tools""");
    }

    [Fact]
    public async Task GenerateElevatedAsync_ThreadsThePreparedInputs_IntoElevatedWorkerParams()
    {
        string? capturedParamsJson = null;
        var worker = new FakeElevatedWorker(onParams: json => capturedParamsJson = json);
        var svc = new BootImageGenerationService(
            NullLogger<BootImageGenerationService>.Instance,
            isElevatedOverride: () => false,
            startElevatedProcessOverride: worker.Start);

        await svc.GenerateElevatedAsync(
            _ => Task.FromResult(new BootImageGenerationService.GenerationInputs(@"C:\client", @"C:\hp-tools")),
            pfxBytes: null,
            outputDirectory: Path.GetTempPath());

        capturedParamsJson.Should().Contain(@"""ClientBinariesPath"":""C:\\client""").And.Contain(@"""HpFirmwareToolsPath"":""C:\\hp-tools""");
    }
}
