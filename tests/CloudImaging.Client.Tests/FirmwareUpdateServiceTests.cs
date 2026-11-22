using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using CloudImaging.Client.Services;
using CloudImaging.Client.Services.Firmware;
using CloudImaging.Client.Services.Setup;
using CloudImaging.Client.Tests.TestDoubles;
using CloudImaging.Client.ViewModels;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models.Drivers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudImaging.Client.Tests;

public sealed class FirmwareUpdateServiceTests : IDisposable
{
    private static readonly byte[] Bin = Encoding.UTF8.GetBytes("bios 01.02.04");
    private readonly string _staging = Path.Combine(Path.GetTempPath(), $"ci-fw-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_staging))
        {
            Directory.Delete(_staging, recursive: true);
        }
    }

    private static SessionFirmwareDescriptor Descriptor(string sha256) => new()
    {
        PlatformId = "8EF5",
        Version = "01.02.04",
        InstalledVersion = "01.01.19",
        FileName = "Y88_01020400.bin",
        Sha256 = sha256,
        SizeBytes = Bin.Length,
        DownloadUrl = "https://storage.test/files/Hp/firmware-8EF5/01.02.04/Y88_01020400.bin?sig=x",
        DownloadUrlExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
    };

    private static FirmwareDeviceState Device(PowerSource power = PowerSource.Ac, string? bios = "Y88 Ver. 01.01.19", bool powerShell = true, string? tools = @"X:\Program Files\WindowsPowerShell\Modules\HP.Firmware") =>
        new(power, bios, powerShell, tools);

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    [Theory]
    [InlineData((byte)1, (byte)8, PowerSource.Ac)]
    [InlineData((byte)0, (byte)1, PowerSource.Battery)]
    [InlineData((byte)255, (byte)128, PowerSource.Ac)]
    [InlineData((byte)255, (byte)255, PowerSource.Unknown)]
    public void MapPowerSource_ReadsTheAcLine_AndTreatsADeviceWithoutBatteryAsMains(byte acLine, byte batteryFlag, PowerSource expected) =>
        DevicePostureDetector.MapPowerSource(acLine, batteryFlag).Should().Be(expected);

    [Fact]
    public void Precheck_LeavesAnUpToDateBiosAlone_WithoutAWarning()
    {
        var result = FirmwareUpdateService.Precheck(Descriptor("x"), Device(bios: "Y88 Ver. 01.02.04"));

        result.Should().Be(new FirmwareUpdateResult(false, 0, "Up to date: the device has BIOS 01.02.04."));
    }

    [Fact]
    public void Precheck_SkipsOnBattery_WithAWarning()
    {
        var result = FirmwareUpdateService.Precheck(Descriptor("x"), Device(power: PowerSource.Battery))!;

        result.WarningCount.Should().Be(1);
        result.StatusNote.Should().StartWith("Skipped: the device runs on battery.");
    }

    [Fact]
    public void Precheck_SkipsWhenTheBootMediaHasNoHpTools()
    {
        FirmwareUpdateService.Precheck(Descriptor("x"), Device(tools: null))!.StatusNote
            .Should().Be("Skipped: HP firmware tools are not in the boot media. Generate a new boot image with Media Builder and include them.");
    }

    [Theory]
    [InlineData("CI-RESULT: STAGED", true, 0, "Staged 01.01.19 to 01.02.04. It flashes at the restart into Windows setup.")]
    [InlineData("This system is already running BIOS version 01.02.04", false, 0, "Up to date: the device already has BIOS 01.02.04.")]
    [InlineData("CI-RESULT: ERROR You cannot flash while 'Lock BIOS Version' is enabled.", false, 1, "Skipped: the BIOS version is locked in the BIOS settings.")]
    [InlineData("CI-RESULT: ERROR HP Sure Admin is enabled, and authentication is required to update the BIOS.", false, 1, "Skipped: the BIOS requires HP Sure Admin authorization for updates.")]
    [InlineData("CI-RESULT: ERROR Could not locate EFI partition, too many candidates.", false, 1, "Skipped: the device has more than one EFI system partition.")]
    [InlineData("CI-RESULT: ERROR Could not locate EFI partition.", false, 1, "Not updated: the EFI system partition was not found.")]
    [InlineData("CI-RESULT: ERROR The firmware does not mach the target platform.", false, 1, "Not updated: the BIOS file does not match this model.")]
    [InlineData("CI-RESULT: ERROR Something new.", false, 1, "Not updated: Something new.")]
    public void MapOutcome_TurnsHpMessagesIntoOutcomes(string line, bool staged, int warnings, string note) =>
        FirmwareUpdateService.MapOutcome(["Loading modules", line], "01.01.19", "01.02.04").Should().Be(new FirmwareUpdateResult(staged, warnings, note));

    [Fact]
    public async Task RunAsync_VerifiesTheFile_KeepsHpsFileName_AndPassesItThroughTheEnvironment()
    {
        var processes = new FakeRestrictedProcessRunner { ReadScript = File.ReadAllText };
        string? stagedFile = null;
        processes.Respond = request =>
        {
            stagedFile = request.Environment[FirmwareUpdateService.FileVariable];
            File.ReadAllBytes(stagedFile).Should().Equal(Bin);
            return new RestrictedProcessResult(RestrictedProcessOutcome.Exited, 0, TimeSpan.FromSeconds(5), ["CI-RESULT: STAGED"]);
        };
        using var http = new HttpClient(new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Bin) }));
        var service = new FirmwareUpdateService(processes, http, NullLogger.Instance);

        var result = await service.RunAsync(Descriptor(Sha(Bin)), Device(), _staging, _ => { }, CancellationToken.None);

        result.Staged.Should().BeTrue();
        Path.GetFileName(stagedFile).Should().Be("Y88_01020400.bin");
        var run = processes.Runs.Should().ContainSingle().Subject;
        run.ScriptText.Should().Contain("Update-HPFirmware -File $env:CI_BIOS_FILE -Offline -BitLocker ignore -Quiet");
        run.ScriptText.Should().Contain("Import-Module HP.Private, HP.Utility, HP.ClientManagement, HP.Firmware");
        run.Request.Arguments.Should().NotContain("Y88_01020400.bin");
        File.Exists(stagedFile).Should().BeFalse("the staged copy is on the EFI system partition, not here");
    }

    [Fact]
    public async Task RunAsync_ABadChecksum_NeverRunsHpsTool()
    {
        var processes = new FakeRestrictedProcessRunner();
        using var http = new HttpClient(new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Bin) }));
        var service = new FirmwareUpdateService(processes, http, NullLogger.Instance);

        var result = await service.RunAsync(Descriptor(new string('0', 64)), Device(), _staging, _ => { }, CancellationToken.None);

        result.Should().Be(new FirmwareUpdateResult(false, 1, "Not updated: the BIOS could not be downloaded or did not match its checksum."));
        processes.Runs.Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.NoContent, FirmwareFetchStatus.None)]
    [InlineData(HttpStatusCode.NotFound, FirmwareFetchStatus.None)]
    [InlineData(HttpStatusCode.Conflict, FirmwareFetchStatus.PipelineEnding)]
    [InlineData(HttpStatusCode.Gone, FirmwareFetchStatus.Unavailable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, FirmwareFetchStatus.Failed)]
    public async Task GetFirmwareAsync_MapsTheGatewayStatus(HttpStatusCode status, FirmwareFetchStatus expected)
    {
        using var http = new HttpClient(new RecordingHandler(_ => new HttpResponseMessage(status))) { BaseAddress = new Uri("https://gw.example.com") };

        var fetch = await new DeviceGatewayApiClient(http).GetFirmwareAsync(Guid.NewGuid());

        fetch.Should().Be(new FirmwareFetch(expected, null));
    }

    [Fact]
    public async Task RunAsync_IncompleteDetails_AreAWarning_NotAnException()
    {
        var processes = new FakeRestrictedProcessRunner();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Bin) });
        using var http = new HttpClient(handler);
        var service = new FirmwareUpdateService(processes, http, NullLogger.Instance);

        var result = await service.RunAsync(Descriptor(Sha(Bin)) with { DownloadUrl = "files/Y88_01020400.bin" }, Device(), _staging, _ => { }, CancellationToken.None);

        result.Should().Be(new FirmwareUpdateResult(false, 1, "Not updated: the BIOS details from Cloud Imaging are incomplete."));
        handler.Requests.Should().BeEmpty();
        processes.Runs.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_AToolThatCannotStart_IsAWarning_AndLeavesNothingBehind()
    {
        var processes = new FakeRestrictedProcessRunner { Respond = _ => throw new System.ComponentModel.Win32Exception(5) };
        using var http = new HttpClient(new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Bin) }));
        var service = new FirmwareUpdateService(processes, http, NullLogger.Instance);

        var result = await service.RunAsync(Descriptor(Sha(Bin)), Device(), _staging, _ => { }, CancellationToken.None);

        result.Staged.Should().BeFalse();
        result.WarningCount.Should().Be(1);
        Directory.Exists(_staging).Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_Cancelled_StopsThePipeline_InsteadOfReportingATimeOut()
    {
        using var cts = new CancellationTokenSource();
        var processes = new FakeRestrictedProcessRunner
        {
            Respond = _ =>
            {
                cts.Cancel();
                return new RestrictedProcessResult(RestrictedProcessOutcome.TimedOut, null, TimeSpan.FromSeconds(1), []);
            },
        };
        using var http = new HttpClient(new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Bin) }));
        var service = new FirmwareUpdateService(processes, http, NullLogger.Instance);

        var run = () => service.RunAsync(Descriptor(Sha(Bin)), Device(), _staging, _ => { }, cts.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void SuccessNote_RemindsAboutAcPower_WhenTheBiosFlashesAtTheRestart()
    {
        ImagingWorkflowViewModel.SuccessNote(0, 0, firmwareStaged: true).Should().Be(ResultsViewModel.FirmwareStagedMessage);
        ImagingWorkflowViewModel.SuccessNote(0, 1, firmwareStaged: false).Should().Be(ResultsViewModel.FirmwareNotUpdatedMessage);
        ImagingWorkflowViewModel.SuccessNote(0, 0, firmwareStaged: false).Should().BeNull();
    }
}
